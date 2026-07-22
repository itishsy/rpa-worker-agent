using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Seebot.WorkerAgent.Core.Configuration;

namespace Seebot.WorkerAgent.Core.Vmware;

public sealed partial class VmxNetworkConfigurationService : IVmxNetworkConfigurationService
{
    private const int MaximumWriteAttempts = 5;
    private static readonly TimeSpan WriteRetryInterval = TimeSpan.FromMilliseconds(500);

    private readonly ILogger<VmxNetworkConfigurationService> _logger;

    public VmxNetworkConfigurationService(ILogger<VmxNetworkConfigurationService> logger)
    {
        _logger = logger;
    }

    public async Task<VmxNetworkConfigurationResult> ApplyAsync(
        string vmxPath,
        VmrunOptions options,
        CancellationToken cancellationToken)
    {
        var netType = options.NetType?.Trim().ToLowerInvariant() ?? "";
        if (netType is not ("nat" or "bridged" or "hostonly" or "custom"))
        {
            return new(false, $"Unsupported Vmrun.NetType '{options.NetType}'.");
        }

        if (string.IsNullOrWhiteSpace(vmxPath) || !File.Exists(vmxPath))
        {
            return new(false, $"VMX file does not exist: {vmxPath}");
        }

        Exception? lastException = null;
        for (var attempt = 1; attempt <= MaximumWriteAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await UpdateFileAsync(vmxPath, netType, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "VMX 网络类型更新成功. VmxPath={VmxPath}, NetType={NetType}",
                    vmxPath,
                    netType);
                return new(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastException = ex;
                _logger.LogWarning(
                    ex,
                    "VMX 网络类型写入失败，准备重试. VmxPath={VmxPath}, NetType={NetType}, Attempt={Attempt}, MaxAttempts={MaxAttempts}",
                    vmxPath,
                    netType,
                    attempt,
                    MaximumWriteAttempts);

                if (attempt < MaximumWriteAttempts)
                {
                    await Task.Delay(WriteRetryInterval, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return new(false, $"Failed to update ethernet0.connectionType: {lastException?.Message}");
    }

    private static async Task UpdateFileAsync(
        string vmxPath,
        string netType,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(vmxPath, cancellationToken).ConfigureAwait(false);
        var encoding = DetectEncoding(bytes);
        var preambleLength = GetPreambleLength(bytes, encoding);
        var content = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        var replacement = $"ethernet0.connectionType = \"{netType}\"";
        var updated = EthernetConnectionTypeRegex().IsMatch(content)
            ? EthernetConnectionTypeRegex().Replace(content, replacement)
            : AppendSetting(content, replacement);

        var temporaryPath = vmxPath + ".network.tmp";
        try
        {
            var body = encoding.GetBytes(updated);
            var preamble = encoding.GetPreamble();
            var output = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, output, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, output, preamble.Length, body.Length);
            await File.WriteAllBytesAsync(temporaryPath, output, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, vmxPath, overwrite: true);

            var verified = await File.ReadAllTextAsync(vmxPath, encoding, cancellationToken).ConfigureAwait(false);
            var match = EthernetConnectionTypeRegex().Match(verified);
            if (!match.Success || !string.Equals(match.Groups[1].Value, netType, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("VMX network configuration verification failed.");
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // A later run can safely overwrite this deterministic temporary file.
            }
        }
    }

    private static string AppendSetting(string content, string setting)
    {
        if (content.Length == 0) return setting + Environment.NewLine;
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return content.EndsWith("\r", StringComparison.Ordinal) || content.EndsWith("\n", StringComparison.Ordinal)
            ? content + setting + newline
            : content + newline + setting + newline;
    }

    private static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble())) return new UTF8Encoding(true);
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble())) return Encoding.Unicode;
        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble())) return Encoding.BigEndianUnicode;
        // VMX files without a BOM may declare different encodings. Latin-1 provides a one-to-one
        // byte mapping, so unrelated non-ASCII bytes remain unchanged while the ASCII setting is edited.
        return Encoding.Latin1;
    }

    private static int GetPreambleLength(byte[] bytes, Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        return preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
    }

    [GeneratedRegex("(?im)^\\s*ethernet0\\.connectionType\\s*=\\s*\"([^\"]*)\"\\s*$")]
    private static partial Regex EthernetConnectionTypeRegex();
}
