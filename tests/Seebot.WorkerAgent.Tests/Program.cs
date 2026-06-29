using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Text;
using System.Text.Json;
using Seebot.WorkerAgent.Core;
using Seebot.WorkerAgent.Core.Backup;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Domain;
using Seebot.WorkerAgent.Core.Guest;
using Seebot.WorkerAgent.Core.Reporting;
using Seebot.WorkerAgent.Core.Scheduler;
using Seebot.WorkerAgent.Core.Scheduling;
using Seebot.WorkerAgent.Core.Startup;
using Seebot.WorkerAgent.Core.Storage;
using Seebot.WorkerAgent.Core.Snapshot;
using Seebot.WorkerAgent.Core.Switching;
using Seebot.WorkerAgent.Core.Vmware;
using Microsoft.AspNetCore.Builder;
using AgentProgram = Seebot.WorkerAgent.Service.Program;

var tests = new (string Name, Action Body)[]
{
    ("Host registers core services and hosted services", HostRegistersCoreServicesAndHostedServices),
    ("Project file copies appsettings to output", ProjectFileCopiesAppsettingsToOutput),
    ("Complete configuration validates successfully", CompleteConfigurationValidatesSuccessfully),
    ("Missing RunnerStatusUrl fails validation", MissingRunnerStatusUrlFailsValidation),
    ("RunnerStatusUrl must use port 9090", RunnerStatusUrlMustUsePort9090),
    ("Missing guest db backup path fails validation", MissingGuestDbBackupPathFailsValidation),
    ("Missing SnapshotName fails validation", MissingSnapshotNameFailsValidation),
    ("Configured snapshot name must use versioned ProfileId format", ConfiguredSnapshotNameMustUseVersionedProfileIdFormat),
    ("SnapshotName matching versioned format passes validation", SnapshotNameMatchingVersionedFormatPassesValidation),
    ("SnapshotName with wrong ProfileId prefix fails validation", SnapshotNameWithWrongProfileIdPrefixFailsValidation),
    ("SnapshotName without date version suffix fails validation", SnapshotNameWithoutDateVersionSuffixFailsValidation),
    ("Duplicate profileId inside one VM fails validation", DuplicateProfileIdInsideVmFailsValidation),
    ("Duplicate workerId across host fails validation", DuplicateWorkerIdAcrossHostFailsValidation),
    ("RunnerStatusCode values match legacy runner contract", RunnerStatusCodeValuesMatchLegacyRunnerContract),
    ("ErrorCodes include P0 required codes", ErrorCodesIncludeP0RequiredCodes),
    ("SwitchTransactionStatus contains P0 lifecycle states", SwitchTransactionStatusContainsP0LifecycleStates),
    ("WorkerStateEvaluator blocks Running and Upgrading before switch", WorkerStateEvaluatorBlocksRunningAndUpgradingBeforeSwitch),
    ("WorkerStateEvaluator allows only Runnable switch candidates before stop", WorkerStateEvaluatorAllowsOnlyRunnableSwitchCandidatesBeforeStop),
    ("WorkerStateEvaluator evaluates ready after VM start", WorkerStateEvaluatorEvaluatesReadyAfterVmStart),
    ("WorkerStateEvaluator rejects candidate when current profile has pending work", WorkerStateEvaluatorRejectsCandidateWhenCurrentProfileHasPendingWork),
    ("WorkerStateEvaluator rejects candidate when idle duration is too short", WorkerStateEvaluatorRejectsCandidateWhenIdleDurationIsTooShort),
    ("WorkerStateEvaluator rejects quarantined VM candidates", WorkerStateEvaluatorRejectsQuarantinedVmCandidates),
    ("LocalStore initializes SQLite tables", LocalStoreInitializesSqliteTables),
    ("LocalStore upserts and queries VM state", LocalStoreUpsertsAndQueriesVmState),
    ("LocalStore creates and queries switch transactions", LocalStoreCreatesAndQueriesSwitchTransactions),
    ("LocalStore updates switch transaction status", LocalStoreUpdatesSwitchTransactionStatus),
    ("LocalStore incomplete transaction query excludes terminal states", LocalStoreIncompleteTransactionQueryExcludesTerminalStates),
    ("VmrunService passes listSnapshots arguments in order and parses output", VmrunServicePassesListSnapshotsArgumentsInOrderAndParsesOutput),
    ("VmrunService passes stop soft and hard arguments", VmrunServicePassesStopSoftAndHardArguments),
    ("VmrunService passes revertToSnapshot arguments", VmrunServicePassesRevertToSnapshotArguments),
    ("VmrunService passes start nogui arguments", VmrunServicePassesStartNoguiArguments),
    ("VmrunService passes shared folder arguments", VmrunServicePassesSharedFolderArguments),
    ("VmrunService exposes non-zero exit code as failure", VmrunServiceExposesNonZeroExitCodeAsFailure),
    ("VmrunService passes snapshot arguments", VmrunServicePassesSnapshotArguments),
    ("VmrunService passes deleteSnapshot arguments", VmrunServicePassesDeleteSnapshotArguments),
    ("SchedulerClient pending query includes profileId and bearer token", SchedulerClientPendingQueryIncludesProfileIdAndBearerToken),
    ("SchedulerClient capabilities posts profile capability list", SchedulerClientCapabilitiesPostsProfileCapabilityList),
    ("SchedulerClient VM status includes current profile snapshot and runner status", SchedulerClientVmStatusIncludesCurrentProfileSnapshotAndRunnerStatus),
    ("SchedulerClient backup result includes backedUpDirectories", SchedulerClientBackupResultIncludesBackedUpDirectories),
    ("SchedulerClient non-success response exposes diagnostic error", SchedulerClientNonSuccessResponseExposesDiagnosticError),
    ("GuestWorkerClient status calls RunnerStatusUrl and maps Running", GuestWorkerClientStatusCallsRunnerStatusUrlAndMapsRunning),
    ("GuestWorkerClient kill success response parses runner details", GuestWorkerClientKillSuccessResponseParsesRunnerDetails),
    ("GuestWorkerClient kill worker running returns explicit failure", GuestWorkerClientKillWorkerRunningReturnsExplicitFailure),
    ("GuestWorkerClient transport failure exposes diagnostic error", GuestWorkerClientTransportFailureExposesDiagnosticError),
    ("LogBackupService creates timestamped target directories and copies four folders", LogBackupServiceCreatesTimestampedTargetDirectoriesAndCopiesFourFolders),
    ("LogBackupService writes manifest with required directories", LogBackupServiceWritesManifestWithRequiredDirectories),
    ("LogBackupService returns failure when a directory copy fails", LogBackupServiceReturnsFailureWhenDirectoryCopyFails),
    ("StartupValidator fails when base snapshot is missing", StartupValidatorFailsWhenBaseSnapshotIsMissing),
    ("StartupValidator fails when base snapshot name differs from VM name", StartupValidatorFailsWhenBaseSnapshotNameDiffersFromVmName),
    ("StartupValidator marks missing profile snapshot as not ready", StartupValidatorMarksMissingProfileSnapshotAsNotReady),
    ("StartupValidator marks all profiles ready when snapshots exist", StartupValidatorMarksAllProfilesReadyWhenSnapshotsExist),
    ("VmSwitchService does not kill backup or vmrun when runner is Running", VmSwitchServiceDoesNotKillBackupOrVmrunWhenRunnerIsRunning),
    ("VmSwitchService does not backup or vmrun when kill returns WORKER_RUNNING", VmSwitchServiceDoesNotBackupOrVmrunWhenKillReturnsWorkerRunning),
    ("VmSwitchService stops before VM stop when backup fails and force revert is false", VmSwitchServiceStopsBeforeVmStopWhenBackupFailsAndForceRevertIsFalse),
    ("VmSwitchService success path executes ordered external actions", VmSwitchServiceSuccessPathExecutesOrderedExternalActions),
    ("VmSwitchService marks mismatch after ready as WORKER_PROFILE_MISMATCH", VmSwitchServiceMarksMismatchAfterReadyAsWorkerProfileMismatch),
    ("PoolSchedulerService does not switch when no pending tasks", PoolSchedulerServiceDoesNotSwitchWhenNoPendingTasks),
    ("PoolSchedulerService switches a compatible idle VM when pending", PoolSchedulerServiceSwitchesCompatibleIdleVmWhenPending),
    ("PoolSchedulerService starts at most one switch per cycle", PoolSchedulerServiceStartsAtMostOneSwitchPerCycle),
    ("PoolSchedulerService skips Running VM candidates", PoolSchedulerServiceSkipsRunningVmCandidates),
    ("PoolSchedulerService does not preempt current profile with pending tasks", PoolSchedulerServiceDoesNotPreemptCurrentProfileWithPendingTasks),
    ("PoolSchedulerService handles higher priority profile first", PoolSchedulerServiceHandlesHigherPriorityProfileFirst),
    ("PoolSchedulerService reverts to General when no pending tasks and VM is not on General", PoolSchedulerServiceRevertsToGeneralWhenNoPendingTasksAndVmIsNotGeneral),
    ("PoolSchedulerService does not revert when VM already on General and no pending tasks", PoolSchedulerServiceDoesNotRevertWhenVmAlreadyOnGeneral),
    ("CapabilityReportService reports all VM profile capabilities", CapabilityReportServiceReportsAllVmProfileCapabilities),
    ("SnapshotNameGenerator generates first sequence number when no existing snapshot for today", SnapshotNameGeneratorGeneratesFirstSequenceNumber),
    ("SnapshotNameGenerator increments sequence when today snapshot already exists", SnapshotNameGeneratorIncrementsSequence),
    ("SnapshotNameGenerator does not conflict with different profile on same date", SnapshotNameGeneratorIgnoresDifferentProfile),
    ("SnapshotNameGenerator does not conflict with same profile on different date", SnapshotNameGeneratorIgnoresDifferentDate),
    ("SnapshotNameGenerator picks max sequence when multiple exist for today", SnapshotNameGeneratorPicksMaxPlusOne),
    ("SnapshotUpdateService success path executes steps in order", SnapshotUpdateServiceSuccessPathExecutesStepsInOrder),
    ("SnapshotUpdateService fails at revert when snapshot not found", SnapshotUpdateServiceFailsAtRevert),
    ("SnapshotUpdateService fails when runner is not ready after start", SnapshotUpdateServiceFailsWhenRunnerNotReady),
    ("P0 integration success closes config to switch and reporting loop", P0IntegrationSuccessClosesConfigToSwitchAndReportingLoop),
    ("P0 integration runner Running does not switch", P0IntegrationRunnerRunningDoesNotSwitch),
    ("P0 integration kill WORKER_RUNNING does not switch", P0IntegrationKillWorkerRunningDoesNotSwitch),
    ("P0 integration backup failure does not revert", P0IntegrationBackupFailureDoesNotRevert),
    ("P0 integration revert failure marks transaction and VM error", P0IntegrationRevertFailureMarksTransactionAndVmError)
};

foreach (var test in tests)
{
    test.Body();
    Console.WriteLine($"PASS: {test.Name}");
}

static void HostRegistersCoreServicesAndHostedServices()
{
    var builder = AgentProgram.CreateWebApplicationBuilder(Array.Empty<string>());
    var app = builder.Build();
    var hostedServices = app.Services.GetServices<IHostedService>().ToList();

    Assert.NotNull(app.Services.GetRequiredService<IAgentCoreMarker>(), "Core marker service should be registered.");
    Assert.NotEmpty(hostedServices, "At least one hosted service should be registered.");
    Assert.True(
        hostedServices.Any(service => service.GetType().Name == "LocalStoreInitializerService"),
        "LocalStore initializer hosted service should run before reporting services read SQLite tables.");
    Assert.False(
        hostedServices.Any(service => service.GetType().Name == "HeartbeatBackgroundService"),
        "WorkerAgent should not report host heartbeat because runner already reports heartbeat.");
}

static void ProjectFileCopiesAppsettingsToOutput()
{
    var projectXml = File.ReadAllText("rpa-worker-agent.csproj");

    Assert.True(projectXml.Contains("appsettings.json", StringComparison.Ordinal), "Project should include appsettings.json.");
    Assert.True(projectXml.Contains("CopyToOutputDirectory", StringComparison.Ordinal), "Project should copy appsettings.json to build output.");
    Assert.True(projectXml.Contains("CopyToPublishDirectory", StringComparison.Ordinal), "Project should copy appsettings.json to publish output.");
}

static void CompleteConfigurationValidatesSuccessfully()
{
    var options = ValidOptions();

    var result = WorkerAgentOptionsValidator.Validate(options);

    Assert.True(result.IsValid, "Complete configuration should validate successfully.");
}

static void MissingRunnerStatusUrlFailsValidation()
{
    var options = ValidOptions();
    options.VirtualMachines[0].RunnerStatusUrl = "";

    var result = WorkerAgentOptionsValidator.Validate(options);

    Assert.Contains(result.Errors, "RunnerStatusUrl");
}

static void RunnerStatusUrlMustUsePort9090()
{
    var options = ValidOptions();
    options.VirtualMachines[0].RunnerStatusUrl = "http://192.168.100.101:8080/api/robot/start/status";

    var result = WorkerAgentOptionsValidator.Validate(options);

    Assert.Contains(result.Errors, "9090");
}

static void MissingGuestDbBackupPathFailsValidation()
{
    var options = ValidOptions();
    options.VirtualMachines[0].GuestBackupPaths.Db = "";

    var result = WorkerAgentOptionsValidator.Validate(options);

    Assert.Contains(result.Errors, "GuestBackupPaths.Db");
}

static void MissingSnapshotNameFailsValidation()
{
    var options = ValidOptions();
    options.VirtualMachines[0].Profiles[0].SnapshotName = "";

    var result = WorkerAgentOptionsValidator.Validate(options);

    Assert.Contains(result.Errors, "SnapshotName");
}

static void ConfiguredSnapshotNameMustUseVersionedProfileIdFormat()
{
    var options = ValidOptions();
    options.VirtualMachines[0].Profiles[0].SnapshotName = "legacy-versioned-snapshot";

    var result = WorkerAgentOptionsValidator.Validate(options);

    Assert.Contains(result.Errors, "SnapshotName");
}

static void SnapshotNameMatchingVersionedFormatPassesValidation()
{
    var options = ValidOptions();
    options.VirtualMachines[0].Profiles[0].SnapshotName = "rpa-sh-tax-etax.v260624.1";

    var result = WorkerAgentOptionsValidator.Validate(options);

    Assert.True(result.IsValid, "SnapshotName matching ProfileId.vYYMMDD.No format should pass validation.");
}

static void SnapshotNameWithWrongProfileIdPrefixFailsValidation()
{
    var options = ValidOptions();
    options.VirtualMachines[0].Profiles[0].SnapshotName = "other-profile.v260624.1";

    var result = WorkerAgentOptionsValidator.Validate(options);

    Assert.Contains(result.Errors, "SnapshotName");
}

static void SnapshotNameWithoutDateVersionSuffixFailsValidation()
{
    var options = ValidOptions();
    options.VirtualMachines[0].Profiles[0].SnapshotName = "rpa-sh-tax-etax";

    var result = WorkerAgentOptionsValidator.Validate(options);

    Assert.Contains(result.Errors, "SnapshotName");
}

static void DuplicateProfileIdInsideVmFailsValidation()
{
    var options = ValidOptions();
    options.VirtualMachines[0].Profiles.Add(new ProfileOptions
    {
        ProfileId = options.VirtualMachines[0].Profiles[0].ProfileId,
        ProfileName = options.VirtualMachines[0].Profiles[0].ProfileName,
        SnapshotName = "rpa-sh-tax-etax.v260624.2"
    });

    var result = WorkerAgentOptionsValidator.Validate(options);

    Assert.Contains(result.Errors, "ProfileId");
}

static void DuplicateWorkerIdAcrossHostFailsValidation()
{
    var options = ValidOptions();
    options.VirtualMachines[1].WorkerId = options.VirtualMachines[0].WorkerId;

    var result = WorkerAgentOptionsValidator.Validate(options);

    Assert.Contains(result.Errors, "WorkerId");
}

static void RunnerStatusCodeValuesMatchLegacyRunnerContract()
{
    Assert.Equal(0, (int)RunnerStatusCode.New, "New should be 0.");
    Assert.Equal(1, (int)RunnerStatusCode.Runnable, "Runnable should be 1.");
    Assert.Equal(2, (int)RunnerStatusCode.Running, "Running should be 2.");
    Assert.Equal(3, (int)RunnerStatusCode.Closed, "Closed should be 3.");
    Assert.Equal(4, (int)RunnerStatusCode.RobotError, "RobotError should be 4.");
    Assert.Equal(5, (int)RunnerStatusCode.ClientError, "ClientError should be 5.");
    Assert.Equal(6, (int)RunnerStatusCode.Upgrading, "Upgrading should be 6.");
    Assert.Equal(7, (int)RunnerStatusCode.UpgradeFailed, "UpgradeFailed should be 7.");
    Assert.Equal(8, (int)RunnerStatusCode.Offline, "Offline should be 8.");
}

static void ErrorCodesIncludeP0RequiredCodes()
{
    var requiredCodes = new[]
    {
        ErrorCodes.WorkerRunning,
        ErrorCodes.WorkerUpgrading,
        ErrorCodes.LogBackupFailed,
        ErrorCodes.WorkerProfileMismatch
    };

    Assert.Contains(requiredCodes, "WORKER_RUNNING");
    Assert.Contains(requiredCodes, "WORKER_UPGRADING");
    Assert.Contains(requiredCodes, "LOG_BACKUP_FAILED");
    Assert.Contains(requiredCodes, "WORKER_PROFILE_MISMATCH");
}

static void SwitchTransactionStatusContainsP0LifecycleStates()
{
    var expectedStatuses = new[]
    {
        SwitchTransactionStatus.CREATED,
        SwitchTransactionStatus.STOP_RUNNER_DONE,
        SwitchTransactionStatus.LOG_BACKUP_DONE,
        SwitchTransactionStatus.VM_STOP_DONE,
        SwitchTransactionStatus.SNAPSHOT_REVERT_DONE,
        SwitchTransactionStatus.VM_START_DONE,
        SwitchTransactionStatus.WORKER_READY_DONE,
        SwitchTransactionStatus.SUCCESS,
        SwitchTransactionStatus.FAILED,
        SwitchTransactionStatus.NEED_MANUAL_CHECK
    };

    foreach (var status in expectedStatuses)
    {
        Assert.True(Enum.IsDefined(status), $"{status} should be defined.");
    }
}

static void WorkerStateEvaluatorBlocksRunningAndUpgradingBeforeSwitch()
{
    Assert.True(WorkerStateEvaluator.IsRunnerBusy(RunnerStatusCode.Running), "Running should be busy.");
    Assert.True(WorkerStateEvaluator.IsRunnerUpgradeLocked(RunnerStatusCode.Upgrading), "Upgrading should be upgrade locked.");
    Assert.False(WorkerStateEvaluator.CanSwitchBeforeStop(RunnerStatusCode.Running), "Running should not switch before stop.");
    Assert.False(WorkerStateEvaluator.CanSwitchBeforeStop(RunnerStatusCode.Upgrading), "Upgrading should not switch before stop.");
}

static void WorkerStateEvaluatorAllowsOnlyRunnableSwitchCandidatesBeforeStop()
{
    Assert.True(WorkerStateEvaluator.CanSwitchBeforeStop(RunnerStatusCode.Runnable), "Runnable should be a switch candidate before stop.");
    Assert.False(WorkerStateEvaluator.CanSwitchBeforeStop(RunnerStatusCode.New), "New should not be a switch candidate before stop.");
    Assert.False(WorkerStateEvaluator.CanSwitchBeforeStop(RunnerStatusCode.Closed), "Closed should not be a switch candidate before stop.");
}

static void WorkerStateEvaluatorEvaluatesReadyAfterVmStart()
{
    Assert.Equal(WorkerReadyEvaluationKind.Ready, WorkerStateEvaluator.EvaluateReadyAfterVmStart(RunnerStatusCode.Runnable).Kind, "Runnable should be ready.");
    Assert.Equal(WorkerReadyEvaluationKind.Ready, WorkerStateEvaluator.EvaluateReadyAfterVmStart(RunnerStatusCode.Running).Kind, "Running after VM start should be ready.");
    Assert.Equal(WorkerReadyEvaluationKind.Wait, WorkerStateEvaluator.EvaluateReadyAfterVmStart(RunnerStatusCode.New).Kind, "New should wait.");
    Assert.Equal(WorkerReadyEvaluationKind.Wait, WorkerStateEvaluator.EvaluateReadyAfterVmStart(RunnerStatusCode.Upgrading).Kind, "Upgrading should wait.");
    Assert.Equal(ErrorCodes.RunnerClosed, WorkerStateEvaluator.EvaluateReadyAfterVmStart(RunnerStatusCode.Closed).ErrorCode, "Closed should map to RUNNER_CLOSED.");
    Assert.Equal(ErrorCodes.RobotError, WorkerStateEvaluator.EvaluateReadyAfterVmStart(RunnerStatusCode.RobotError).ErrorCode, "RobotError should map to ROBOT_ERROR.");
    Assert.Equal(ErrorCodes.ClientError, WorkerStateEvaluator.EvaluateReadyAfterVmStart(RunnerStatusCode.ClientError).ErrorCode, "ClientError should map to CLIENT_ERROR.");
    Assert.Equal(ErrorCodes.UpgradeFailed, WorkerStateEvaluator.EvaluateReadyAfterVmStart(RunnerStatusCode.UpgradeFailed).ErrorCode, "UpgradeFailed should map to UPGRADE_FAILED.");
    Assert.Equal(ErrorCodes.WorkerOffline, WorkerStateEvaluator.EvaluateReadyAfterVmStart(RunnerStatusCode.Offline).ErrorCode, "Offline should map to WORKER_OFFLINE.");
}

static void WorkerStateEvaluatorRejectsCandidateWhenCurrentProfileHasPendingWork()
{
    var now = DateTimeOffset.Parse("2026-06-19T10:00:00+08:00");
    var vmState = SwitchCandidateState(now.AddSeconds(-60));

    var result = WorkerStateEvaluator.EvaluateSwitchCandidate(vmState, currentProfilePending: true, idleStableSeconds: 30, now);

    Assert.False(result.CanSwitch, "VM should not be switchable while current profile queue still has pending work.");
    Assert.Equal(ErrorCodes.VmNotIdle, result.ErrorCode, "Pending current profile work should map to VM_NOT_IDLE.");
}

static void WorkerStateEvaluatorRejectsCandidateWhenIdleDurationIsTooShort()
{
    var now = DateTimeOffset.Parse("2026-06-19T10:00:00+08:00");
    var vmState = SwitchCandidateState(now.AddSeconds(-10));

    var result = WorkerStateEvaluator.EvaluateSwitchCandidate(vmState, currentProfilePending: false, idleStableSeconds: 30, now);

    Assert.False(result.CanSwitch, "VM should not be switchable before idle duration reaches threshold.");
    Assert.Equal(ErrorCodes.VmNotIdle, result.ErrorCode, "Short idle duration should map to VM_NOT_IDLE.");
}

static void WorkerStateEvaluatorRejectsQuarantinedVmCandidates()
{
    var now = DateTimeOffset.Parse("2026-06-19T10:00:00+08:00");
    var vmState = SwitchCandidateState(now.AddSeconds(-60));
    vmState.IsQuarantined = true;

    var result = WorkerStateEvaluator.EvaluateSwitchCandidate(vmState, currentProfilePending: false, idleStableSeconds: 30, now);

    Assert.False(result.CanSwitch, "Quarantined VM should not be switchable.");
    Assert.Equal(ErrorCodes.WorkerQuarantined, result.ErrorCode, "Quarantined VM should map to WORKER_QUARANTINED.");
}

static VmCurrentState SwitchCandidateState(DateTimeOffset idleSince)
{
    return new VmCurrentState
    {
        VmName = "SR20-2026-6HQ8",
        WorkerId = "rpa-sh-tax-etax-01",
        RunnerStatusCode = RunnerStatusCode.Runnable,
        IsQuarantined = false,
        HasActiveSwitchTransaction = false,
        IdleSince = idleSince
    };
}

static void LocalStoreInitializesSqliteTables()
{
    using var scope = TempDatabase();
    var store = new LocalStore(scope.DatabasePath);

    store.InitializeAsync().GetAwaiter().GetResult();

    Assert.True(SqliteTableExists(scope.DatabasePath, "local_vm_state"), "local_vm_state table should exist.");
    Assert.True(SqliteTableExists(scope.DatabasePath, "local_switch_transaction"), "local_switch_transaction table should exist.");
}

static void LocalStoreUpsertsAndQueriesVmState()
{
    using var scope = TempDatabase();
    var store = new LocalStore(scope.DatabasePath);
    store.InitializeAsync().GetAwaiter().GetResult();

    var now = DateTimeOffset.Parse("2026-06-19T10:00:00+08:00");
    store.UpsertVmStateAsync("SB-VM-001", new VmCurrentState
    {
        VmName = "SR20-2026-6HQ8",
        VmxPath = @"D:\VMs\SR20-2026-6HQ8\SR20-2026-6HQ8.vmx",
        WorkerId = "rpa-sh-tax-etax-01",
        CurrentProfileId = "rpa-sh-tax-etax",
        CurrentSnapshotName = "rpa-sh-tax-etax",
        RunnerStatusCode = RunnerStatusCode.Runnable,
        VmStatus = AgentVmStatus.MONITORING,
        IdleSince = now.AddMinutes(-5),
        IsQuarantined = false,
        UpdatedAt = now
    }).GetAwaiter().GetResult();

    var states = store.GetVmStatesAsync("SB-VM-001").GetAwaiter().GetResult();

    Assert.Equal(1, states.Count, "One VM state should be returned.");
    Assert.Equal("SR20-2026-6HQ8", states[0].VmName, "VM name should round-trip.");
    Assert.Equal(RunnerStatusCode.Runnable, states[0].RunnerStatusCode, "Runner status should round-trip.");
    Assert.Equal(AgentVmStatus.MONITORING, states[0].VmStatus, "VM status should round-trip.");
}

static void LocalStoreCreatesAndQueriesSwitchTransactions()
{
    using var scope = TempDatabase();
    var store = new LocalStore(scope.DatabasePath);
    store.InitializeAsync().GetAwaiter().GetResult();
    var tx = SampleTransaction("tx-001", SwitchTransactionStatus.CREATED);

    store.CreateSwitchTransactionAsync(tx).GetAwaiter().GetResult();
    var loaded = store.GetSwitchTransactionAsync("tx-001").GetAwaiter().GetResult();

    Assert.NotNull(loaded, "Created transaction should be queryable by txId.");
    Assert.Equal(SwitchTransactionStatus.CREATED, loaded!.Status, "Transaction status should round-trip.");
    Assert.Equal("rpa-sh-tax-etax", loaded.TargetProfileId, "Target profile should round-trip.");
}

static void LocalStoreUpdatesSwitchTransactionStatus()
{
    using var scope = TempDatabase();
    var store = new LocalStore(scope.DatabasePath);
    store.InitializeAsync().GetAwaiter().GetResult();
    store.CreateSwitchTransactionAsync(SampleTransaction("tx-002", SwitchTransactionStatus.CREATED)).GetAwaiter().GetResult();

    store.UpdateSwitchTransactionAsync(
        "tx-002",
        SwitchTransactionStatus.LOG_BACKUP_DONE,
        step: "backup-complete",
        errorCode: null,
        errorMessage: null,
        updatedAt: DateTimeOffset.Parse("2026-06-19T10:03:00+08:00")).GetAwaiter().GetResult();

    var loaded = store.GetSwitchTransactionAsync("tx-002").GetAwaiter().GetResult();

    Assert.Equal(SwitchTransactionStatus.LOG_BACKUP_DONE, loaded!.Status, "Updated transaction status should be queryable.");
    Assert.Equal("backup-complete", loaded.Step, "Updated step should be queryable.");
}

static void LocalStoreIncompleteTransactionQueryExcludesTerminalStates()
{
    using var scope = TempDatabase();
    var store = new LocalStore(scope.DatabasePath);
    store.InitializeAsync().GetAwaiter().GetResult();
    store.CreateSwitchTransactionAsync(SampleTransaction("tx-active", SwitchTransactionStatus.VM_STOP_DONE)).GetAwaiter().GetResult();
    store.CreateSwitchTransactionAsync(SampleTransaction("tx-success", SwitchTransactionStatus.SUCCESS)).GetAwaiter().GetResult();
    store.CreateSwitchTransactionAsync(SampleTransaction("tx-failed", SwitchTransactionStatus.FAILED)).GetAwaiter().GetResult();

    var incomplete = store.GetIncompleteSwitchTransactionsAsync("SB-VM-001").GetAwaiter().GetResult();

    Assert.Equal(1, incomplete.Count, "Only non-terminal transactions should be returned.");
    Assert.Equal("tx-active", incomplete[0].TransactionId, "Active transaction should be returned.");
}

static void VmrunServicePassesListSnapshotsArgumentsInOrderAndParsesOutput()
{
    var runner = new FakeProcessRunner(new VmrunCommandResult(
        ExitCode: 0,
        StandardOutput: """
Total snapshots: 2
BaseClean
rpa-sh-tax-etax
""",
        StandardError: "",
        Duration: TimeSpan.FromMilliseconds(25),
        CommandName: "listSnapshots"));
    var service = NewVmrunService(runner);

    var snapshots = service.ListSnapshotsAsync(@"D:\VMs With Spaces\SR20\SR20.vmx", CancellationToken.None).GetAwaiter().GetResult();

    Assert.SequenceEqual(new[] { "listSnapshots", @"D:\VMs With Spaces\SR20\SR20.vmx" }, runner.LastCommand!.Arguments, "listSnapshots argument order should match vmrun contract.");
    Assert.SequenceEqual(new[] { "BaseClean", "rpa-sh-tax-etax" }, snapshots, "listSnapshots output should skip the Total snapshots line.");
}

static void VmrunServicePassesStopSoftAndHardArguments()
{
    var runner = new FakeProcessRunner(SuccessResult("stop"));
    var service = NewVmrunService(runner);

    service.StopVmAsync(@"D:\VMs With Spaces\SR20\SR20.vmx", VmStopMode.Soft, CancellationToken.None).GetAwaiter().GetResult();
    Assert.SequenceEqual(new[] { "stop", @"D:\VMs With Spaces\SR20\SR20.vmx", "soft" }, runner.LastCommand!.Arguments, "soft stop arguments should match vmrun contract.");

    service.StopVmAsync(@"D:\VMs With Spaces\SR20\SR20.vmx", VmStopMode.Hard, CancellationToken.None).GetAwaiter().GetResult();
    Assert.SequenceEqual(new[] { "stop", @"D:\VMs With Spaces\SR20\SR20.vmx", "hard" }, runner.LastCommand!.Arguments, "hard stop arguments should match vmrun contract.");
}

static void VmrunServicePassesRevertToSnapshotArguments()
{
    var runner = new FakeProcessRunner(SuccessResult("revertToSnapshot"));
    var service = NewVmrunService(runner);

    service.RevertToSnapshotAsync(@"D:\VMs With Spaces\SR20\SR20.vmx", "rpa-sh-tax-etax", CancellationToken.None).GetAwaiter().GetResult();

    Assert.SequenceEqual(new[] { "revertToSnapshot", @"D:\VMs With Spaces\SR20\SR20.vmx", "rpa-sh-tax-etax" }, runner.LastCommand!.Arguments, "revertToSnapshot arguments should match vmrun contract.");
}

static void VmrunServicePassesStartNoguiArguments()
{
    var runner = new FakeProcessRunner(SuccessResult("start"));
    var service = NewVmrunService(runner);

    service.StartVmAsync(@"D:\VMs With Spaces\SR20\SR20.vmx", noGui: true, CancellationToken.None).GetAwaiter().GetResult();

    Assert.SequenceEqual(new[] { "start", @"D:\VMs With Spaces\SR20\SR20.vmx", "nogui" }, runner.LastCommand!.Arguments, "start nogui arguments should match vmrun contract.");
}

static void VmrunServicePassesSharedFolderArguments()
{
    var runner = new FakeProcessRunner(SuccessResult("addSharedFolder"));
    var service = NewVmrunService(runner);

    service.AddSharedFolderAsync(
        @"D:\VMs With Spaces\SR20\SR20.vmx",
        "SR20-2026-6HQ8",
        @"D:\seebot work\shared",
        CancellationToken.None).GetAwaiter().GetResult();

    Assert.SequenceEqual(
        new[] { "addSharedFolder", @"D:\VMs With Spaces\SR20\SR20.vmx", "SR20-2026-6HQ8", @"D:\seebot work\shared" },
        runner.LastCommand!.Arguments,
        "shared folder arguments should include VMX path, share name, and host path as independent arguments.");
}

static void VmrunServiceExposesNonZeroExitCodeAsFailure()
{
    var runner = new FakeProcessRunner(new VmrunCommandResult(
        ExitCode: 42,
        StandardOutput: "",
        StandardError: "snapshot not found",
        Duration: TimeSpan.FromMilliseconds(5),
        CommandName: "revertToSnapshot"));
    var service = NewVmrunService(runner);

    var exception = Assert.Throws<VmrunCommandException>(() =>
        service.RevertToSnapshotAsync(@"D:\VMs\SR20\SR20.vmx", "missing-snapshot", CancellationToken.None).GetAwaiter().GetResult());

    Assert.Equal(42, exception.Result.ExitCode, "Non-zero exit code should be preserved.");
    Assert.Equal("snapshot not found", exception.Result.StandardError, "Standard error should be preserved.");
}

static void VmrunServicePassesSnapshotArguments()
{
    var runner = new FakeProcessRunner(SuccessResult("snapshot"));
    var service = NewVmrunService(runner);

    service.CreateSnapshotAsync(@"D:\VMs With Spaces\SR20\SR20.vmx", "rpa-sh-tax-etax.v260624.1", CancellationToken.None).GetAwaiter().GetResult();

    Assert.SequenceEqual(new[] { "snapshot", @"D:\VMs With Spaces\SR20\SR20.vmx", "rpa-sh-tax-etax.v260624.1" }, runner.LastCommand!.Arguments, "snapshot arguments should match vmrun contract.");
}

static void VmrunServicePassesDeleteSnapshotArguments()
{
    var runner = new FakeProcessRunner(SuccessResult("deleteSnapshot"));
    var service = NewVmrunService(runner);

    service.DeleteSnapshotAsync(@"D:\VMs With Spaces\SR20\SR20.vmx", "rpa-sh-tax-etax.v260624.1", CancellationToken.None).GetAwaiter().GetResult();

    Assert.SequenceEqual(new[] { "deleteSnapshot", @"D:\VMs With Spaces\SR20\SR20.vmx", "rpa-sh-tax-etax.v260624.1" }, runner.LastCommand!.Arguments, "deleteSnapshot arguments should match vmrun contract.");
}

static void SchedulerClientPendingQueryIncludesProfileIdAndBearerToken()
{
    var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = JsonContent("""{"code":0,"message":"ok","data":["rpa-sh-tax-etax"]}""")
    });
    var client = NewSchedulerClient(handler);

    var response = client.QueryPendingTasksAsync("rpa-sh-tax-etax-001", CancellationToken.None).GetAwaiter().GetResult();

    Assert.Equal(1, response.Count, "Pending response should deserialize profileId list.");
    Assert.Equal(true, response[0].HasTask, "Pending profile should be treated as a task.");
    Assert.Equal("rpa-sh-tax-etax", response[0].ProfileId, "Pending response should deserialize profileId.");
    Assert.Equal("/robot/client/task/findTaskProfileCode/rpa-sh-tax-etax-001", handler.LastRequest!.RequestUri!.AbsolutePath, "Pending query path should match scheduler contract.");
    Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme, "Authorization scheme should be Bearer.");
    Assert.Equal("scheduler-token", handler.LastRequest.Headers.Authorization.Parameter, "Authorization token should match scheduler AccessToken.");
}

static void SchedulerClientCapabilitiesPostsProfileCapabilityList()
{
    var handler = new FakeHttpMessageHandler(OkResponse());
    var client = NewSchedulerClient(handler);

    client.ReportCapabilitiesAsync(
    [
        new HostProfileCapabilityRequest
        {
            HostName = "SR20 Host Agent",
            MachineCode = "rpa-sh-tax-etax-001",
            ProfileId = "rpa-sh-tax-etax",
            ProfileName = "上海税务电子税局",
            SnapshotName = "rpa-sh-tax-etax.v260624.1"
        }
    ], CancellationToken.None).GetAwaiter().GetResult();

    Assert.True(handler.LastRequestBody!.Contains("上海税务电子税局", StringComparison.Ordinal), "Capability profileName should be written as UTF-8 Chinese text in the request body.");
    Assert.False(handler.LastRequestBody.Contains("\\u4e0a", StringComparison.OrdinalIgnoreCase), "Capability profileName should not be escaped as unicode sequences.");

    using var json = handler.LastJsonDocument();
    Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind, "Capability report should serialize as a JSON array.");
    var item = json.RootElement[0];
    Assert.Equal("SR20 Host Agent", item.GetProperty("hostName").GetString(), "Capability hostName should be serialized.");
    Assert.Equal("rpa-sh-tax-etax-001", item.GetProperty("machineCode").GetString(), "Capability machineCode should be serialized.");
    Assert.Equal("rpa-sh-tax-etax", item.GetProperty("profileId").GetString(), "Capability profileId should be serialized.");
    Assert.Equal("上海税务电子税局", item.GetProperty("profileName").GetString(), "Capability profileName should be serialized.");
    Assert.Equal("rpa-sh-tax-etax.v260624.1", item.GetProperty("snapshotName").GetString(), "Capability snapshotName should be serialized.");
}

static void SchedulerClientVmStatusIncludesCurrentProfileSnapshotAndRunnerStatus()
{
    var handler = new FakeHttpMessageHandler(OkResponse());
    var client = NewSchedulerClient(handler);

    client.ReportVmStatusAsync(new VmStatusReportRequest
    {
        HostId = "HOST-SR20-001",
        VmName = "SR20-2026-6HQ8",
        WorkerId = "rpa-sh-tax-etax-001",
        CurrentProfileId = "rpa-sh-tax-etax",
        CurrentSnapshotName = "rpa-sh-tax-etax",
        AgentVmStatus = "MONITORING",
        RunnerStatusCode = 1,
        RunnerStatusName = "Runnable",
        RunnerStatusDesc = "Runnable",
        IsQuarantined = false,
        LastHeartbeatTime = "2026-06-17 10:00:00"
    }, CancellationToken.None).GetAwaiter().GetResult();

    using var json = handler.LastJsonDocument();
    Assert.Equal("rpa-sh-tax-etax", json.RootElement.GetProperty("currentProfileId").GetString(), "VM status currentProfileId should be serialized.");
    Assert.Equal("rpa-sh-tax-etax", json.RootElement.GetProperty("currentSnapshotName").GetString(), "VM status currentSnapshotName should be serialized.");
    Assert.Equal(1, json.RootElement.GetProperty("runnerStatusCode").GetInt32(), "VM status runnerStatusCode should be serialized.");
}

static void SchedulerClientBackupResultIncludesBackedUpDirectories()
{
    var handler = new FakeHttpMessageHandler(OkResponse());
    var client = NewSchedulerClient(handler);

    client.ReportDirectoryBackupResultAsync(new DirectoryBackupResultRequest
    {
        TxId = "SWITCH-20260617-0001",
        HostId = "HOST-SR20-001",
        VmName = "SR20-2026-6HQ8",
        WorkerId = "rpa-sh-tax-etax-001",
        FromProfileId = "rpa-sh-social-portal",
        ToProfileId = "rpa-sh-tax-etax",
        FirstTaskId = 123456,
        Success = true,
        BackupPath = @"D:\seebot-agent\work\SR20-2026-6HQ8\20260617100000",
        BackedUpDirectories = ["cache", "db", "file", "logs"],
        FileCount = 128,
        TotalBytes = 98234212
    }, CancellationToken.None).GetAwaiter().GetResult();

    using var json = handler.LastJsonDocument();
    var directories = json.RootElement.GetProperty("backedUpDirectories").EnumerateArray().Select(item => item.GetString()).ToArray();
    Assert.SequenceEqual(new[] { "cache", "db", "file", "logs" }, directories!, "Backup result should include backedUpDirectories.");
}

static void SchedulerClientNonSuccessResponseExposesDiagnosticError()
{
    var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError)
    {
        Content = JsonContent("server exploded")
    });
    var client = NewSchedulerClient(handler);

    var exception = Assert.Throws<SchedulerClientException>(() =>
        client.ReportSwitchLogAsync(new WorkerSwitchLogRequest
        {
            TxId = "SWITCH-20260617-0001",
            HostId = "HOST-SR20-001",
            VmName = "SR20-2026-6HQ8",
            WorkerId = "rpa-sh-tax-etax-001",
            ToProfileId = "rpa-sh-tax-etax",
            ToSnapshotName = "rpa-sh-tax-etax",
            Status = "FAILED",
            StartedAt = "2026-06-17 09:45:00"
        }, CancellationToken.None).GetAwaiter().GetResult());

    Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode, "SchedulerClientException should preserve status code.");
    Assert.Contains(new[] { exception.ResponseBody }, "server exploded");
}

static void GuestWorkerClientStatusCallsRunnerStatusUrlAndMapsRunning()
{
    var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = JsonContent("""{"code":200,"message":"ok","data":2}""")
    });
    var client = NewGuestWorkerClient(handler);

    var response = client.GetRunnerStatusAsync(GuestVm(), CancellationToken.None).GetAwaiter().GetResult();

    Assert.Equal("http://192.168.100.101:9090/api/robot/start/status", handler.LastRequest!.RequestUri!.ToString(), "Runner status should call RunnerStatusUrl.");
    Assert.Equal(RunnerStatusCode.Running, response.RunnerStatusCode, "runnerStatusCode 2 should map to RunnerStatusCode.Running.");
}

static void GuestWorkerClientKillSuccessResponseParsesRunnerDetails()
{
    var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = JsonContent("""{"code":200,"message":"ok","data":0}""")
    });
    var client = NewGuestWorkerClient(handler);

    var response = client.KillRunnerAsync(GuestVm(), "SWITCH-20260617-0001", "SNAPSHOT_SWITCH", 30, CancellationToken.None).GetAwaiter().GetResult();

    Assert.Equal("http://192.168.100.101:9090/api/robot/kill", handler.LastRequest!.RequestUri!.ToString(), "Kill should call RunnerKillUrl.");
    using var json = handler.LastJsonDocument();
    Assert.Equal("SNAPSHOT_SWITCH", json.RootElement.GetProperty("reason").GetString(), "Kill request should include reason.");
    Assert.Equal("SWITCH-20260617-0001", json.RootElement.GetProperty("txId").GetString(), "Kill request should include txId.");
    Assert.Equal(30, json.RootElement.GetProperty("deadlineSeconds").GetInt32(), "Kill request should include deadlineSeconds.");
    Assert.True(response.Success, "Kill success should deserialize.");
    Assert.Equal(null, response.ErrorCode, "Successful kill should have no errorCode.");
}

static void GuestWorkerClientKillWorkerRunningReturnsExplicitFailure()
{
    var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = JsonContent("""{"code":200,"message":"runner is executing task","data":1}""")
    });
    var client = NewGuestWorkerClient(handler);

    var response = client.KillRunnerAsync(GuestVm(), "SWITCH-20260617-0001", "SNAPSHOT_SWITCH", 30, CancellationToken.None).GetAwaiter().GetResult();

    Assert.False(response.Success, "Running runner kill should return explicit failure.");
    Assert.Equal(ErrorCodes.ExecutorStopFailed, response.ErrorCode, "Non-zero kill result should expose executor stop failure.");
    Assert.Contains(new[] { response.Message ?? "" }, "data=1");
}

static void GuestWorkerClientTransportFailureExposesDiagnosticError()
{
    var handler = new FakeHttpMessageHandler(new HttpRequestException("connection refused"));
    var client = NewGuestWorkerClient(handler);

    var exception = Assert.Throws<GuestWorkerClientException>(() =>
        client.GetRunnerStatusAsync(GuestVm(), CancellationToken.None).GetAwaiter().GetResult());

    Assert.Contains(new[] { exception.Message }, "connection refused");
    Assert.Contains(new[] { exception.RequestUrl }, "9090");
}

static void LogBackupServiceCreatesTimestampedTargetDirectoriesAndCopiesFourFolders()
{
    using var scope = TempDirectory();
    var vmrun = new FakeVmrunService();
    var service = new LogBackupService(vmrun);
    var timestamp = DateTimeOffset.Parse("2026-06-19T10:11:12+08:00");
    var vm = BackupVm(scope.DirectoryPath);

    var result = service.BackupAsync(vm, BackupTransaction(), timestamp, CancellationToken.None).GetAwaiter().GetResult();

    Assert.True(result.Success, "Backup should succeed when script and copy both succeed.");
    Assert.Equal(Path.Combine(scope.DirectoryPath, "SR20-2026-6HQ8", "20260619101112"), result.TargetPath, "Backup target path should use yyyyMMddHHmmss.");
    Assert.Equal(1, vmrun.ProgramCalls.Count, "One PowerShell program should be invoked on the guest.");
    Assert.Contains(vmrun.ProgramCalls[0], "-EncodedCommand", "Arguments should use -EncodedCommand.");
    Assert.Equal(1, vmrun.CopyCalls.Count, "One CopyFileFromGuestToHost call should copy the zip.");
    Assert.True(vmrun.CopyCalls[0].GuestPath.EndsWith("20260619101112.zip", StringComparison.OrdinalIgnoreCase), "Copied guest path should be the timestamped zip.");
    Assert.True(File.Exists(Path.Combine(result.TargetPath, "20260619101112.zip")), "Zip file should exist at target path.");
    Assert.True(File.Exists(Path.Combine(result.TargetPath, "20260619101112_backup.ps1")), "ps1 audit script should be saved at target path.");
}

static void LogBackupServiceWritesManifestWithRequiredDirectories()
{
    using var scope = TempDirectory();
    var service = new LogBackupService(new FakeVmrunService());
    var timestamp = DateTimeOffset.Parse("2026-06-19T10:11:12+08:00");
    var vm = BackupVm(scope.DirectoryPath);

    var result = service.BackupAsync(vm, BackupTransaction(), timestamp, CancellationToken.None).GetAwaiter().GetResult();

    var manifestPath = Path.Combine(result.TargetPath, "backup_manifest.json");
    Assert.True(File.Exists(manifestPath), "backup_manifest.json should be written.");
    using var json = JsonDocument.Parse(File.ReadAllText(manifestPath));
    Assert.Equal("SWITCH-20260617-0001", json.RootElement.GetProperty("txId").GetString(), "Manifest txId should match transaction.");
    Assert.Equal("HOST-SR20-001", json.RootElement.GetProperty("hostId").GetString(), "Manifest hostId should match transaction.");
    Assert.Equal("SR20-2026-6HQ8", json.RootElement.GetProperty("vmName").GetString(), "Manifest vmName should match VM config.");
    Assert.Equal(scope.DirectoryPath, json.RootElement.GetProperty("workPath").GetString(), "Manifest workPath should match HostWorkPath.");
    Assert.Equal(@"C:\seebot", json.RootElement.GetProperty("guestWorkPath").GetString(), "Manifest guestWorkPath should match GuestWorkPath.");
    Assert.Equal(@"C:\seebot\db", json.RootElement.GetProperty("sources").GetProperty("db").GetString(), "Manifest should include db source.");
    Assert.Equal("20260619101112.zip", json.RootElement.GetProperty("zipFileName").GetString(), "Manifest should record the zip file name.");
    var directories = json.RootElement.GetProperty("directories").EnumerateArray().Select(item => item.GetString()).ToArray();
    Assert.SequenceEqual(new[] { "cache", "db", "file", "logs" }, directories!, "Manifest directories should include cache/db/file/logs.");
    Assert.True(json.RootElement.GetProperty("success").GetBoolean(), "Manifest success should be true on success.");
}

static void LogBackupServiceReturnsFailureWhenDirectoryCopyFails()
{
    using var scope = TempDirectory();
    var vm = BackupVm(scope.DirectoryPath);
    var guestZipPath = Path.Combine(vm.GuestWorkPath, "20260619101112.zip").Replace('/', '\\');
    var vmrun = new FakeVmrunService(failOnGuestPath: guestZipPath);
    var service = new LogBackupService(vmrun);

    var result = service.BackupAsync(vm, BackupTransaction(), DateTimeOffset.Parse("2026-06-19T10:11:12+08:00"), CancellationToken.None).GetAwaiter().GetResult();

    Assert.False(result.Success, "Backup should fail when zip copy fails.");
    Assert.Equal(ErrorCodes.LogBackupFailed, result.ErrorCode, "Failure should use LOG_BACKUP_FAILED.");
    Assert.False(string.IsNullOrEmpty(result.ErrorMessage), "Failure should include error message.");
    using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.TargetPath, "backup_manifest.json")));
    Assert.False(json.RootElement.GetProperty("success").GetBoolean(), "Manifest success should be false on copy failure.");
}

static void StartupValidatorFailsWhenBaseSnapshotIsMissing()
{
    using var scope = TempDirectory();
    var options = StartupOptions(scope.DirectoryPath);
    var validator = new StartupValidator(new FakeVmrunService(snapshots: ["rpa-sh-tax-etax.v260624.1"]));

    var result = validator.ValidateAndBuildCapabilitiesAsync(options, "2026-06-19 10:00:00", CancellationToken.None).GetAwaiter().GetResult();

    Assert.False(result.IsValid, "Missing base snapshot should fail startup validation.");
    Assert.Contains(result.Errors, "BaseSnapshotName");
}

static void StartupValidatorFailsWhenBaseSnapshotNameDiffersFromVmName()
{
    using var scope = TempDirectory();
    var options = StartupOptions(scope.DirectoryPath);
    options.VirtualMachines[0].BaseSnapshotName = "BaseClean";
    var validator = new StartupValidator(new FakeVmrunService(snapshots: ["BaseClean", "rpa-sh-tax-etax.v260624.1"]));

    var result = validator.ValidateAndBuildCapabilitiesAsync(options, "2026-06-19 10:00:00", CancellationToken.None).GetAwaiter().GetResult();

    Assert.False(result.IsValid, "BaseSnapshotName differing from VM name should fail startup validation.");
    Assert.Contains(result.Errors, "must match VM name");
}

static void StartupValidatorMarksMissingProfileSnapshotAsNotReady()
{
    using var scope = TempDirectory();
    var options = StartupOptions(scope.DirectoryPath);
    var validator = new StartupValidator(new FakeVmrunService(snapshots: ["SR20-2026-6HQ8"]));

    var result = validator.ValidateAndBuildCapabilitiesAsync(options, "2026-06-19 10:00:00", CancellationToken.None).GetAwaiter().GetResult();
    var profile = result.Capabilities.Vms[0].Profiles[0];

    Assert.False(result.IsValid, "Missing profile snapshot should fail startup validation.");
    Assert.False(profile.SnapshotExists, "Missing profile snapshot should set snapshotExists=false.");
    Assert.False(string.Equals("READY", profile.ValidationStatus, StringComparison.OrdinalIgnoreCase), "Missing profile snapshot should not be READY.");
}

static void StartupValidatorMarksAllProfilesReadyWhenSnapshotsExist()
{
    using var scope = TempDirectory();
    var options = StartupOptions(scope.DirectoryPath);
    var validator = new StartupValidator(new FakeVmrunService(snapshots: ["SR20-2026-6HQ8", "rpa-sh-tax-etax.v260624.1", "rpa-sh-social-portal.v260624.1"]));

    var result = validator.ValidateAndBuildCapabilitiesAsync(options, "2026-06-19 10:00:00", CancellationToken.None).GetAwaiter().GetResult();

    Assert.True(result.IsValid, "All configured snapshots should pass startup validation.");
    Assert.Equal("HOST-SR20-001", result.Capabilities.HostId, "Capabilities should include hostId.");
    Assert.Equal("SR20 Host Agent", result.Capabilities.AgentName, "Capabilities should include agentName.");
    Assert.Equal("SR20-2026-6HQ8", result.Capabilities.Vms[0].VmName, "Capabilities should include vmName.");
    Assert.Equal("rpa-sh-tax-etax-001", result.Capabilities.Vms[0].WorkerId, "Capabilities should include workerId.");
    Assert.Equal("SR20-2026-6HQ8", result.Capabilities.Vms[0].BaseSnapshotName, "Capabilities should include baseSnapshotName.");
    Assert.True(result.Capabilities.Vms[0].Profiles.All(profile => profile.SnapshotExists), "All profile snapshots should exist.");
    Assert.True(result.Capabilities.Vms[0].Profiles.All(profile => profile.ValidationStatus == "READY"), "All profiles should be READY.");
}

static void VmSwitchServiceDoesNotKillBackupOrVmrunWhenRunnerIsRunning()
{
    var recorder = new ActionRecorder();
    var guest = new RecordingGuestWorkerClient(recorder)
    {
        StatusResponses =
        [
            RunnerStatus("rpa-sh-tax-etax-001", "rpa-sh-social-portal", RunnerStatusCode.Running, currentTaskId: 123456)
        ]
    };
    var backup = new RecordingBackupService(recorder);
    var vmrun = new RecordingSwitchVmrunService(recorder);
    var service = NewVmSwitchService(guest, backup, vmrun, new RecordingLocalStore(recorder));

    var result = service.SwitchAsync(SwitchRequest(), CancellationToken.None).GetAwaiter().GetResult();

    Assert.False(result.Success, "Running runner should cancel switch.");
    Assert.Equal(ErrorCodes.WorkerRunning, result.ErrorCode, "Running runner should report WORKER_RUNNING.");
    Assert.False(recorder.Actions.Any(action => action is "kill" or "backup" or "vmrun-stop" or "vmrun-revert" or "vmrun-start"), "Running path should not call kill, backup, or vmrun.");
}

static void VmSwitchServiceDoesNotBackupOrVmrunWhenKillReturnsWorkerRunning()
{
    var recorder = new ActionRecorder();
    var guest = new RecordingGuestWorkerClient(recorder)
    {
        StatusResponses =
        [
            RunnerStatus("rpa-sh-tax-etax-001", "rpa-sh-social-portal", RunnerStatusCode.Runnable)
        ],
        KillResponse = new KillRunnerResponse
        {
            Success = false,
            ErrorCode = ErrorCodes.WorkerRunning,
            BeforeRunnerStatusCode = RunnerStatusCode.Running,
            CurrentTaskId = 123456,
            Message = "runner is executing task"
        }
    };
    var backup = new RecordingBackupService(recorder);
    var vmrun = new RecordingSwitchVmrunService(recorder);

    var result = NewVmSwitchService(guest, backup, vmrun, new RecordingLocalStore(recorder))
        .SwitchAsync(SwitchRequest(), CancellationToken.None).GetAwaiter().GetResult();

    Assert.False(result.Success, "WORKER_RUNNING kill result should cancel switch.");
    Assert.Equal(ErrorCodes.WorkerRunning, result.ErrorCode, "Kill WORKER_RUNNING should be preserved.");
    Assert.SequenceEqual(new[] { "create-tx", "get-status", "kill", "update:FAILED" }, recorder.Actions, "Kill running path should stop before backup/vmrun.");
}

static void VmSwitchServiceStopsBeforeVmStopWhenBackupFailsAndForceRevertIsFalse()
{
    var recorder = new ActionRecorder();
    var guest = ReadyGuest(recorder);
    var backup = new RecordingBackupService(recorder)
    {
        Result = new LogBackupResult
        {
            Success = false,
            ErrorCode = ErrorCodes.LogBackupFailed,
            ErrorMessage = "copy failed",
            TargetPath = @"D:\backup"
        }
    };
    var vmrun = new RecordingSwitchVmrunService(recorder);

    var result = NewVmSwitchService(guest, backup, vmrun, new RecordingLocalStore(recorder), forceRevertWhenBackupFailed: false)
        .SwitchAsync(SwitchRequest(), CancellationToken.None).GetAwaiter().GetResult();

    Assert.False(result.Success, "Backup failure should fail switch when force revert is false.");
    Assert.Equal(ErrorCodes.LogBackupFailed, result.ErrorCode, "Backup failure should report LOG_BACKUP_FAILED.");
    Assert.False(recorder.Actions.Any(action => action.StartsWith("vmrun-", StringComparison.Ordinal)), "Backup failure should not stop/revert/start VM.");
}

static void VmSwitchServiceSuccessPathExecutesOrderedExternalActions()
{
    var recorder = new ActionRecorder();
    var guest = ReadyGuest(recorder);
    var backup = new RecordingBackupService(recorder);
    var vmrun = new RecordingSwitchVmrunService(recorder);

    var result = NewVmSwitchService(guest, backup, vmrun, new RecordingLocalStore(recorder))
        .SwitchAsync(SwitchRequest(), CancellationToken.None).GetAwaiter().GetResult();

    Assert.True(result.Success, "Happy path should succeed.");
    Assert.SequenceEqual(
        new[]
        {
            "create-tx",
            "get-status",
            "kill",
            "update:STOP_RUNNER_DONE",
            "backup",
            "update:LOG_BACKUP_DONE",
            "vmrun-stop",
            "update:VM_STOP_DONE",
            "vmrun-revert",
            "update:SNAPSHOT_REVERT_DONE",
            "vmrun-start",
            "update:VM_START_DONE",
            "get-status",
            "update:WORKER_READY_DONE",
            "update:SUCCESS"
        },
        recorder.Actions,
        "Successful switch should call external actions in strict order.");
}

static void VmSwitchServiceMarksMismatchAfterReadyAsWorkerProfileMismatch()
{
    var recorder = new ActionRecorder();
    var guest = new RecordingGuestWorkerClient(recorder)
    {
        StatusResponses =
        [
            RunnerStatus("rpa-sh-tax-etax-001", "rpa-sh-social-portal", RunnerStatusCode.Runnable),
            RunnerStatus("other-worker", "rpa-sh-tax-etax", RunnerStatusCode.Runnable)
        ],
        KillResponse = KillSuccess()
    };

    var result = NewVmSwitchService(guest, new RecordingBackupService(recorder), new RecordingSwitchVmrunService(recorder), new RecordingLocalStore(recorder))
        .SwitchAsync(SwitchRequest(), CancellationToken.None).GetAwaiter().GetResult();

    Assert.False(result.Success, "Worker/profile mismatch should fail switch.");
    Assert.Equal(ErrorCodes.WorkerProfileMismatch, result.ErrorCode, "Mismatch should report WORKER_PROFILE_MISMATCH.");
    Assert.Equal("update:FAILED", recorder.Actions.Last(), "Mismatch should mark transaction failed.");
}

static void PoolSchedulerServiceDoesNotSwitchWhenNoPendingTasks()
{
    var now = DateTimeOffset.Parse("2026-06-20T10:00:00+08:00");
    var scheduler = new RecordingSchedulerClient();
    var switchService = new RecordingVmSwitchService();
    var store = new RecordingLocalStore(new ActionRecorder())
    {
        VmStates = [SchedulerVmState("SR20-2026-6HQ8", "rpa-sh-social-portal", RunnerStatusCode.Runnable, now.AddSeconds(-60))]
    };

    var result = NewPoolScheduler(scheduler, switchService, store, SchedulerWorkerOptions(), now)
        .RunOneCycleAsync(CancellationToken.None).GetAwaiter().GetResult();

    Assert.False(result.SwitchStarted, "No pending profile tasks should not start a switch.");
    Assert.Equal(0, switchService.Requests.Count, "Switch service should not be called when no profiles have pending work.");
}

static void PoolSchedulerServiceSwitchesCompatibleIdleVmWhenPending()
{
    var now = DateTimeOffset.Parse("2026-06-20T10:00:00+08:00");
    var scheduler = new RecordingSchedulerClient
    {
        Pending =
        {
            ["rpa-sh-tax-etax"] = Pending("rpa-sh-tax-etax", priority: 5, oldestQueuedAt: "2026-06-20 09:00:00")
        }
    };
    var switchService = new RecordingVmSwitchService();
    var store = new RecordingLocalStore(new ActionRecorder())
    {
        VmStates = [SchedulerVmState("SR20-2026-6HQ8", "rpa-sh-social-portal", RunnerStatusCode.Runnable, now.AddSeconds(-60))]
    };

    var result = NewPoolScheduler(scheduler, switchService, store, SchedulerWorkerOptions(), now)
        .RunOneCycleAsync(CancellationToken.None).GetAwaiter().GetResult();

    Assert.True(result.SwitchStarted, "Pending target profile should switch one compatible idle VM.");
    Assert.Equal("rpa-sh-tax-etax", result.TargetProfileId, "Result should expose selected target profile.");
    Assert.Equal(1, switchService.Requests.Count, "Exactly one switch should be started.");
    Assert.Equal("SR20-2026-6HQ8", switchService.Requests[0].Vm.Name, "Compatible VM should be selected.");
    Assert.Equal("rpa-sh-tax-etax.v260624.1", switchService.Requests[0].TargetSnapshotName, "Target snapshot should come from profile SnapshotName.");
}

static void PoolSchedulerServiceStartsAtMostOneSwitchPerCycle()
{
    var now = DateTimeOffset.Parse("2026-06-20T10:00:00+08:00");
    var scheduler = new RecordingSchedulerClient
    {
        Pending =
        {
            ["rpa-sh-tax-etax"] = Pending("rpa-sh-tax-etax", priority: 5, oldestQueuedAt: "2026-06-20 09:00:00")
        }
    };
    var switchService = new RecordingVmSwitchService();
    var store = new RecordingLocalStore(new ActionRecorder())
    {
        VmStates =
        [
            SchedulerVmState("SR20-2026-6HQ8", "rpa-sh-social-portal", RunnerStatusCode.Runnable, now.AddSeconds(-60)),
            SchedulerVmState("SR20-2026-7JK9", "rpa-sh-social-portal", RunnerStatusCode.Runnable, now.AddSeconds(-60))
        ]
    };

    var result = NewPoolScheduler(scheduler, switchService, store, SchedulerWorkerOptions(), now)
        .RunOneCycleAsync(CancellationToken.None).GetAwaiter().GetResult();

    Assert.True(result.SwitchStarted, "One idle VM should be switched.");
    Assert.Equal(1, switchService.Requests.Count, "A single scheduling cycle must start at most one switch.");
}

static void PoolSchedulerServiceSkipsRunningVmCandidates()
{
    var now = DateTimeOffset.Parse("2026-06-20T10:00:00+08:00");
    var scheduler = new RecordingSchedulerClient
    {
        Pending =
        {
            ["rpa-sh-tax-etax"] = Pending("rpa-sh-tax-etax", priority: 5, oldestQueuedAt: "2026-06-20 09:00:00")
        }
    };
    var switchService = new RecordingVmSwitchService();
    var store = new RecordingLocalStore(new ActionRecorder())
    {
        VmStates = [SchedulerVmState("SR20-2026-6HQ8", "rpa-sh-social-portal", RunnerStatusCode.Running, now.AddSeconds(-60))]
    };

    var result = NewPoolScheduler(scheduler, switchService, store, SchedulerWorkerOptions(), now)
        .RunOneCycleAsync(CancellationToken.None).GetAwaiter().GetResult();

    Assert.False(result.SwitchStarted, "Running VM should not be selected for switching.");
    Assert.Equal(0, switchService.Requests.Count, "Switch service should not be called for Running VM candidates.");
}

static void PoolSchedulerServiceDoesNotPreemptCurrentProfileWithPendingTasks()
{
    var now = DateTimeOffset.Parse("2026-06-20T10:00:00+08:00");
    var scheduler = new RecordingSchedulerClient
    {
        Pending =
        {
            ["rpa-sh-tax-etax"] = Pending("rpa-sh-tax-etax", priority: 5, oldestQueuedAt: "2026-06-20 09:00:00"),
            ["rpa-sh-social-portal"] = Pending("rpa-sh-social-portal", priority: 1, oldestQueuedAt: "2026-06-20 08:30:00")
        }
    };
    var switchService = new RecordingVmSwitchService();
    var store = new RecordingLocalStore(new ActionRecorder())
    {
        VmStates = [SchedulerVmState("SR20-2026-6HQ8", "rpa-sh-social-portal", RunnerStatusCode.Runnable, now.AddSeconds(-60))]
    };

    var result = NewPoolScheduler(scheduler, switchService, store, SchedulerWorkerOptions(), now)
        .RunOneCycleAsync(CancellationToken.None).GetAwaiter().GetResult();

    Assert.False(result.SwitchStarted, "VM should not be preempted while its current profile still has pending work.");
    Assert.Equal(0, switchService.Requests.Count, "Switch service should not be called when current profile queue is not empty.");
}

static void PoolSchedulerServiceHandlesHigherPriorityProfileFirst()
{
    var now = DateTimeOffset.Parse("2026-06-20T10:00:00+08:00");
    var scheduler = new RecordingSchedulerClient
    {
        Pending =
        {
            ["rpa-sh-tax-etax"] = Pending("rpa-sh-tax-etax", priority: 1, oldestQueuedAt: "2026-06-20 08:00:00"),
            ["rpa-sh-social-portal"] = Pending("rpa-sh-social-portal", priority: 9, oldestQueuedAt: "2026-06-20 09:30:00")
        }
    };
    var switchService = new RecordingVmSwitchService();
    var store = new RecordingLocalStore(new ActionRecorder())
    {
        VmStates = [SchedulerVmState("SR20-2026-6HQ8", "rpa-sh-idle", RunnerStatusCode.Runnable, now.AddSeconds(-60))]
    };

    var result = NewPoolScheduler(scheduler, switchService, store, SchedulerWorkerOptions(), now)
        .RunOneCycleAsync(CancellationToken.None).GetAwaiter().GetResult();

    Assert.True(result.SwitchStarted, "Higher priority profile should be handled first even when another profile is older.");
    Assert.Equal("rpa-sh-social-portal", result.TargetProfileId, "Higher priority target should be selected.");
    Assert.Equal("rpa-sh-social-portal", switchService.Requests[0].TargetProfileId, "Switch request should use the selected higher priority profile.");
}

static void PoolSchedulerServiceRevertsToGeneralWhenNoPendingTasksAndVmIsNotGeneral()
{
    var now = DateTimeOffset.Parse("2026-06-20T10:00:00+08:00");
    var scheduler = new RecordingSchedulerClient();
    var switchService = new RecordingVmSwitchService();
    var store = new RecordingLocalStore(new ActionRecorder())
    {
        VmStates = [SchedulerVmState("SR20-2026-6HQ8", "rpa-sh-social-portal", RunnerStatusCode.Runnable, now.AddSeconds(-60))]
    };
    var options = SchedulerWorkerOptions();
    options.Agent.GeneralProfileId = "rpa-sh-general";
    options.VirtualMachines[0].Profiles.Add(new ProfileOptions
    {
        ProfileId = "rpa-sh-general",
        ProfileName = "General",
        SnapshotName = "rpa-sh-general.v260624.1"
    });

    var result = NewPoolScheduler(scheduler, switchService, store, options, now)
        .RunOneCycleAsync(CancellationToken.None).GetAwaiter().GetResult();

    Assert.True(result.SwitchStarted, "No pending tasks with non-General VM should trigger revert to General.");
    Assert.Equal("rpa-sh-general", result.TargetProfileId, "Revert target should be GeneralProfileId.");
    Assert.Equal(1, switchService.Requests.Count, "Exactly one switch to General should be started.");
    Assert.Equal("rpa-sh-general.v260624.1", switchService.Requests[0].TargetSnapshotName, "Snapshot should match the General profile.");
}

static void PoolSchedulerServiceDoesNotRevertWhenVmAlreadyOnGeneral()
{
    var now = DateTimeOffset.Parse("2026-06-20T10:00:00+08:00");
    var scheduler = new RecordingSchedulerClient();
    var switchService = new RecordingVmSwitchService();
    var store = new RecordingLocalStore(new ActionRecorder())
    {
        VmStates = [SchedulerVmState("SR20-2026-6HQ8", "rpa-sh-general", RunnerStatusCode.Runnable, now.AddSeconds(-60))]
    };
    var options = SchedulerWorkerOptions();
    options.Agent.GeneralProfileId = "rpa-sh-general";
    options.VirtualMachines[0].Profiles.Add(new ProfileOptions
    {
        ProfileId = "rpa-sh-general",
        ProfileName = "General",
        SnapshotName = "rpa-sh-general.v260624.1"
    });

    var result = NewPoolScheduler(scheduler, switchService, store, options, now)
        .RunOneCycleAsync(CancellationToken.None).GetAwaiter().GetResult();

    Assert.False(result.SwitchStarted, "VM already on General should not trigger another revert.");
    Assert.Equal(0, switchService.Requests.Count, "No switch should be started when all VMs are already on General.");
}

static void CapabilityReportServiceReportsAllVmProfileCapabilities()
{
    var scheduler = new RecordingSchedulerClient();
    var options = SchedulerWorkerOptions();
    var service = new CapabilityReportService(
        scheduler,
        options,
        new ListLogger<CapabilityReportService>());

    service.ReportOnceAsync(CancellationToken.None).GetAwaiter().GetResult();

    Assert.Equal(1, scheduler.ReportedCapabilities.Count, "Capabilities should be reported once.");
    Assert.Equal(4, scheduler.ReportedCapabilities[0].Count, "Capability report should include all configured VM profiles.");
    Assert.Equal("SR20 Host Agent", scheduler.ReportedCapabilities[0][0].HostName, "Capability should include hostName.");
    Assert.Equal("rpa-sh-tax-etax-01", scheduler.ReportedCapabilities[0][0].MachineCode, "Capability should include machineCode.");
    Assert.Equal("rpa-sh-tax-etax", scheduler.ReportedCapabilities[0][0].ProfileId, "Capability should include profileId.");
    Assert.Equal("Shanghai Tax", scheduler.ReportedCapabilities[0][0].ProfileName, "Capability should include profileName.");
    Assert.Equal("rpa-sh-tax-etax.v260624.1", scheduler.ReportedCapabilities[0][0].SnapshotName, "Capability should include snapshotName.");
}

static void SnapshotNameGeneratorGeneratesFirstSequenceNumber()
{
    var name = SnapshotNameGenerator.Generate("DongGuan-CA", DateOnly.Parse("2026-06-24"), []);

    Assert.Equal("DongGuan-CA.v260624.1", name, "First snapshot for today should get sequence number 1.");
}

static void SnapshotNameGeneratorIncrementsSequence()
{
    var name = SnapshotNameGenerator.Generate("DongGuan-CA", DateOnly.Parse("2026-06-24"), ["DongGuan-CA.v260624.1"]);

    Assert.Equal("DongGuan-CA.v260624.2", name, "Existing snapshot for today should increment to the next sequence number.");
}

static void SnapshotNameGeneratorIgnoresDifferentProfile()
{
    var name = SnapshotNameGenerator.Generate("DongGuan-CA", DateOnly.Parse("2026-06-24"), ["SuZhou-CA.v260624.1"]);

    Assert.Equal("DongGuan-CA.v260624.1", name, "Snapshot for a different profile should not affect the sequence number.");
}

static void SnapshotNameGeneratorIgnoresDifferentDate()
{
    var name = SnapshotNameGenerator.Generate("DongGuan-CA", DateOnly.Parse("2026-06-24"), ["DongGuan-CA.v260623.3"]);

    Assert.Equal("DongGuan-CA.v260624.1", name, "Snapshot for a different date should not affect today's sequence number.");
}

static void SnapshotNameGeneratorPicksMaxPlusOne()
{
    var name = SnapshotNameGenerator.Generate("DongGuan-CA", DateOnly.Parse("2026-06-24"),
        ["DongGuan-CA.v260624.1", "DongGuan-CA.v260624.3"]);

    Assert.Equal("DongGuan-CA.v260624.4", name, "Generator should pick max existing sequence + 1, not a simple count.");
}

static void SnapshotUpdateServiceSuccessPathExecutesStepsInOrder()
{
    var recorder = new ActionRecorder();
    var vmrunService = new RecordingSnapshotUpdateVmrunService(recorder, nextSnapshots: ["SR20-2026-6HQ8", "rpa-sh-tax-etax.v260624.1"]);
    var guest = new RecordingGuestWorkerClient(recorder)
    {
        StatusResponses = [RunnerStatus("rpa-sh-tax-etax-001", "rpa-sh-tax-etax", RunnerStatusCode.Runnable)],
        KillResponse = KillSuccess()
    };
    var configUpdater = new RecordingConfigFileUpdater(recorder);
    var options = SnapshotUpdateOptions();
    var service = new SnapshotUpdateService(vmrunService, guest, configUpdater, options, new FixedTimeProvider(DateTimeOffset.Parse("2026-06-24T10:00:00+08:00")));

    var result = service.UpdateSnapshotAsync("SR20-2026-6HQ8", "rpa-sh-tax-etax", CancellationToken.None).GetAwaiter().GetResult();

    Assert.True(result.Success, "Snapshot update should succeed when all steps complete.");
    Assert.Equal("rpa-sh-tax-etax.v260624.2", result.NewSnapshotName, "New snapshot name should increment sequence from existing.");
    Assert.SequenceEqual(
        new[] { "vmrun-revert", "vmrun-start", "get-status", "vmrun-stop", "list-snapshots", "vmrun-create", "vmrun-delete", "config-update" },
        recorder.Actions,
        "Steps should execute in the correct order.");
}

static void SnapshotUpdateServiceFailsAtRevert()
{
    var recorder = new ActionRecorder();
    var vmrunService = new RecordingSnapshotUpdateVmrunService(recorder, nextSnapshots: []);
    vmrunService.FailOnRevert = true;
    var guest = new RecordingGuestWorkerClient(recorder)
    {
        StatusResponses = [],
        KillResponse = KillSuccess()
    };
    var configUpdater = new RecordingConfigFileUpdater(recorder);
    var options = SnapshotUpdateOptions();
    var service = new SnapshotUpdateService(vmrunService, guest, configUpdater, options);

    var result = service.UpdateSnapshotAsync("SR20-2026-6HQ8", "rpa-sh-tax-etax", CancellationToken.None).GetAwaiter().GetResult();

    Assert.False(result.Success, "Snapshot update should fail when revert fails.");
    Assert.Equal(ErrorCodes.SnapshotRevertFailed, result.ErrorCode, "Error code should be SNAPSHOT_REVERT_FAILED.");
    Assert.SequenceEqual(new[] { "vmrun-revert" }, recorder.Actions, "Only revert should be attempted before failure.");
}

static void SnapshotUpdateServiceFailsWhenRunnerNotReady()
{
    var recorder = new ActionRecorder();
    var vmrunService = new RecordingSnapshotUpdateVmrunService(recorder, nextSnapshots: []);
    var guest = new RecordingGuestWorkerClient(recorder)
    {
        StatusResponses = [RunnerStatus("rpa-sh-tax-etax-001", "rpa-sh-tax-etax", RunnerStatusCode.Closed)],
        KillResponse = KillSuccess()
    };
    var configUpdater = new RecordingConfigFileUpdater(recorder);
    var options = SnapshotUpdateOptions();
    var service = new SnapshotUpdateService(vmrunService, guest, configUpdater, options, new FixedTimeProvider(DateTimeOffset.Parse("2026-06-24T10:00:00+08:00")));

    var result = service.UpdateSnapshotAsync("SR20-2026-6HQ8", "rpa-sh-tax-etax", CancellationToken.None).GetAwaiter().GetResult();

    Assert.False(result.Success, "Snapshot update should fail when runner is not ready.");
    Assert.Equal(ErrorCodes.RunnerNotReady, result.ErrorCode, "Error code should be RUNNER_NOT_READY.");
    Assert.SequenceEqual(new[] { "vmrun-revert", "vmrun-start", "get-status" }, recorder.Actions, "Stop should not execute when runner is not ready.");
}

static WorkerAgentOptions SnapshotUpdateOptions()
{
    return new WorkerAgentOptions
    {
        Agent = new AgentOptions { HostId = "HOST-SR20-001" },
        Vmrun = new VmrunOptions { DefaultStartNoGui = true },
        VirtualMachines =
        [
            new VirtualMachineOptions
            {
                Name = "SR20-2026-6HQ8",
                VmxPath = @"D:\VMs\SR20-2026-6HQ8\SR20-2026-6HQ8.vmx",
                WorkerId = "rpa-sh-tax-etax-001",
                RunnerStatusUrl = "http://192.168.100.101:9090/api/robot/start/status",
                RunnerKillUrl = "http://192.168.100.101:9090/api/robot/kill",
                Profiles =
                [
                    new ProfileOptions
                    {
                        ProfileId = "rpa-sh-tax-etax",
                        ProfileName = "Shanghai Tax",
                        SnapshotName = "rpa-sh-tax-etax.v260624.1"
                    }
                ]
            }
        ]
    };
}

static void P0IntegrationSuccessClosesConfigToSwitchAndReportingLoop()
{
    using var scope = TempDirectory();
    var now = DateTimeOffset.Parse("2026-06-20T10:00:00+08:00");
    var options = LoadOptionsFromConfiguration(P0IntegrationOptions(scope.DirectoryPath));
    var configValidation = WorkerAgentOptionsValidator.Validate(options);
    Assert.True(configValidation.IsValid, "Loaded integration configuration should pass model validation.");

    var startup = new StartupValidator(new FakeVmrunService(snapshots: ["SR20-2026-6HQ8", "rpa-sh-tax-etax.v260624.1", "rpa-sh-social-portal.v260624.1"]));
    var capabilityScheduler = new RecordingSchedulerClient();
    new CapabilityReportService(
        capabilityScheduler,
        options,
        new ListLogger<CapabilityReportService>())
        .ReportOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
    Assert.Equal(1, capabilityScheduler.ReportedCapabilities.Count, "Startup capability should be reported.");

    var recorder = new ActionRecorder();
    var scheduler = new RecordingSchedulerClient
    {
        Pending =
        {
            ["rpa-sh-tax-etax"] = Pending("rpa-sh-tax-etax", priority: 5, oldestQueuedAt: "2026-06-20 09:00:00")
        }
    };
    var store = new RecordingLocalStore(recorder)
    {
        VmStates = [SchedulerVmState("SR20-2026-6HQ8", "rpa-sh-social-portal", RunnerStatusCode.Runnable, now.AddSeconds(-60))]
    };
    var switchService = NewVmSwitchService(ReadyGuest(recorder), new RecordingBackupService(recorder), new RecordingSwitchVmrunService(recorder), store);
    var result = new PoolSchedulerService(scheduler, ReadyGuest(recorder), new RecordingVmStateRefreshService(), switchService, store, options, new FixedTimeProvider(now))
        .RunOneCycleAsync(CancellationToken.None).GetAwaiter().GetResult();

    Assert.True(result.SwitchStarted, "Pending profile should start the integrated switch.");
    Assert.SequenceEqual(
        new[] { "create-tx", "get-status", "kill", "update:STOP_RUNNER_DONE", "backup", "update:LOG_BACKUP_DONE", "vmrun-stop", "update:VM_STOP_DONE", "vmrun-revert", "update:SNAPSHOT_REVERT_DONE", "vmrun-start", "update:VM_START_DONE", "get-status", "update:WORKER_READY_DONE", "update:SUCCESS" },
        recorder.Actions,
        "Integrated success path should stop runner, backup, vmrun stop/revert/start, wait ready, then succeed.");
}

static void P0IntegrationRunnerRunningDoesNotSwitch()
{
    var recorder = new ActionRecorder();
    var guest = new RecordingGuestWorkerClient(recorder)
    {
        StatusResponses = [RunnerStatus("rpa-sh-tax-etax-001", "rpa-sh-social-portal", RunnerStatusCode.Running, 123456)]
    };
    var vmrun = new RecordingSwitchVmrunService(recorder);
    var result = NewVmSwitchService(guest, new RecordingBackupService(recorder), vmrun, new RecordingLocalStore(recorder))
        .SwitchAsync(SwitchRequest(), CancellationToken.None).GetAwaiter().GetResult();

    Assert.False(result.Success, "Running runner should fail the integrated switch.");
    Assert.Equal(ErrorCodes.WorkerRunning, result.ErrorCode, "Running runner should return WORKER_RUNNING.");
    Assert.False(recorder.Actions.Any(action => action is "kill" or "backup" or "vmrun-stop" or "vmrun-revert" or "vmrun-start"), "Running runner must not be killed, backed up, or reverted.");
}

static void P0IntegrationKillWorkerRunningDoesNotSwitch()
{
    var recorder = new ActionRecorder();
    var guest = new RecordingGuestWorkerClient(recorder)
    {
        StatusResponses = [RunnerStatus("rpa-sh-tax-etax-001", "rpa-sh-social-portal", RunnerStatusCode.Runnable)],
        KillResponse = new KillRunnerResponse
        {
            Success = false,
            ErrorCode = ErrorCodes.WorkerRunning,
            BeforeRunnerStatusCode = RunnerStatusCode.Running,
            CurrentTaskId = 123456,
            Message = "runner is executing task"
        }
    };
    var vmrun = new RecordingSwitchVmrunService(recorder);
    var result = NewVmSwitchService(guest, new RecordingBackupService(recorder), vmrun, new RecordingLocalStore(recorder))
        .SwitchAsync(SwitchRequest(), CancellationToken.None).GetAwaiter().GetResult();

    Assert.False(result.Success, "Kill WORKER_RUNNING should fail the integrated switch.");
    Assert.Equal(ErrorCodes.WorkerRunning, result.ErrorCode, "Kill rejection should preserve WORKER_RUNNING.");
    Assert.False(recorder.Actions.Any(action => action is "backup" or "vmrun-stop" or "vmrun-revert" or "vmrun-start"), "Kill rejection must not backup or revert.");
}

static void P0IntegrationBackupFailureDoesNotRevert()
{
    var recorder = new ActionRecorder();
    var backup = new RecordingBackupService(recorder)
    {
        Result = new LogBackupResult
        {
            Success = false,
            ErrorCode = ErrorCodes.LogBackupFailed,
            ErrorMessage = "copy db failed"
        }
    };
    var vmrun = new RecordingSwitchVmrunService(recorder);
    var result = NewVmSwitchService(ReadyGuest(recorder), backup, vmrun, new RecordingLocalStore(recorder), forceRevertWhenBackupFailed: false)
        .SwitchAsync(SwitchRequest(), CancellationToken.None).GetAwaiter().GetResult();

    Assert.False(result.Success, "Backup failure should fail the integrated switch.");
    Assert.Equal(ErrorCodes.LogBackupFailed, result.ErrorCode, "Backup failure should report LOG_BACKUP_FAILED.");
    Assert.False(recorder.Actions.Any(action => action is "vmrun-stop" or "vmrun-revert" or "vmrun-start"), "Backup failure must not stop, revert, or start VM.");
}

static void P0IntegrationRevertFailureMarksTransactionAndVmError()
{
    var recorder = new ActionRecorder();
    var store = new RecordingLocalStore(recorder);
    var vmrun = new RecordingSwitchVmrunService(recorder)
    {
        FailOnRevert = true
    };

    var result = NewVmSwitchService(ReadyGuest(recorder), new RecordingBackupService(recorder), vmrun, store)
        .SwitchAsync(SwitchRequest(), CancellationToken.None).GetAwaiter().GetResult();

    Assert.False(result.Success, "Revert failure should fail the integrated switch.");
    Assert.Equal(ErrorCodes.SnapshotRevertFailed, result.ErrorCode, "Revert failure should map to SNAPSHOT_REVERT_FAILED.");
    Assert.Equal("update:FAILED", recorder.Actions.Last(), "Revert failure should update transaction to FAILED.");
    Assert.Equal(1, store.UpsertedVmStates.Count, "Revert failure should mark VM state as abnormal.");
    Assert.Equal(AgentVmStatus.ERROR, store.UpsertedVmStates[0].VmStatus, "Failed VM should be marked ERROR.");
    Assert.Equal(ErrorCodes.SnapshotRevertFailed, store.UpsertedVmStates[0].ErrorCode, "Failed VM should preserve revert error code.");
}

static PoolSchedulerService NewPoolScheduler(
    ISchedulerClient scheduler,
    IVmSwitchService switchService,
    ILocalStore store,
    WorkerAgentOptions options,
    DateTimeOffset now)
{
    return new PoolSchedulerService(scheduler, ReadyGuest(new ActionRecorder()), new RecordingVmStateRefreshService(), switchService, store, options, new FixedTimeProvider(now));
}

static VmCurrentState SchedulerVmState(string vmName, string currentProfileId, RunnerStatusCode runnerStatus, DateTimeOffset idleSince)
{
    return new VmCurrentState
    {
        VmName = vmName,
        WorkerId = vmName == "SR20-2026-7JK9" ? "rpa-sh-tax-etax-02" : "rpa-sh-tax-etax-01",
        VmxPath = $@"D:\VMs\{vmName}\{vmName}.vmx",
        VmStatus = AgentVmStatus.MONITORING,
        CurrentProfileId = currentProfileId,
        CurrentSnapshotName = currentProfileId,
        RunnerStatusCode = runnerStatus,
        IdleSince = idleSince,
        UpdatedAt = idleSince
    };
}

static ProfilePendingTaskResponse Pending(string profileId, int priority, string oldestQueuedAt)
{
    return new ProfilePendingTaskResponse
    {
        HasTask = true,
        ProfileId = profileId,
        PendingCount = 100,
        FirstTaskId = 123456,
        Priority = priority,
        OldestQueuedAt = oldestQueuedAt
    };
}

static WorkerAgentOptions SchedulerWorkerOptions()
{
    var options = ValidOptions();
    options.Agent.HostId = "HOST-SR20-001";
    options.Agent.IdleStableSeconds = 30;
    options.Agent.MaxSwitchesPerCycle = 1;
    options.VirtualMachines[1].Profiles.Add(new ProfileOptions
    {
        ProfileId = "rpa-sh-social-portal",
        ProfileName = "Social Portal",
        SnapshotName = "rpa-sh-social-portal.v260624.1"
    });
    return options;
}

static WorkerAgentOptions P0IntegrationOptions(string root)
{
    var vmrunPath = Path.Combine(root, "vmrun.exe");
    var vmxPath = Path.Combine(root, "SR20-2026-6HQ8.vmx");
    File.WriteAllText(vmrunPath, "");
    File.WriteAllText(vmxPath, "");

    var options = ValidOptions();
    options.Agent.HostId = "HOST-SR20-001";
    options.Vmrun.VmrunPath = vmrunPath;
    options.VirtualMachines =
    [
        options.VirtualMachines[0]
    ];
    options.VirtualMachines[0].VmxPath = vmxPath;
    options.VirtualMachines[0].WorkerId = "rpa-sh-tax-etax-001";
    options.VirtualMachines[0].HostWorkPath = root;
    options.VirtualMachines[0].GuestUser = "Administrator";
    options.VirtualMachines[0].GuestPasswordSecret = "password";
    return options;
}

static WorkerAgentOptions LoadOptionsFromConfiguration(WorkerAgentOptions source)
{
    var json = JsonSerializer.Serialize(source, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
    var configuration = new ConfigurationBuilder()
        .AddJsonStream(stream)
        .Build();
    var loaded = new WorkerAgentOptions();
    configuration.Bind(loaded);
    return loaded;
}

static VmSwitchService NewVmSwitchService(
    RecordingGuestWorkerClient guest,
    RecordingBackupService backup,
    RecordingSwitchVmrunService vmrun,
    RecordingLocalStore store,
    bool forceRevertWhenBackupFailed = false)
{
    return new VmSwitchService(guest, backup, vmrun, store, new WorkerAgentOptions
    {
        Agent = new AgentOptions
        {
            ForceRevertWhenBackupFailed = forceRevertWhenBackupFailed,
            WaitVmReadyTimeoutSeconds = 1
        },
        Vmrun = new VmrunOptions
        {
            DefaultStartNoGui = true
        }
    });
}

static VmSwitchRequest SwitchRequest()
{
    return new VmSwitchRequest
    {
        HostId = "HOST-SR20-001",
        Vm = BackupVm(Path.Combine(Path.GetTempPath(), "rpa-worker-agent-switch-tests")),
        FromProfileId = "rpa-sh-social-portal",
        FromSnapshotName = "rpa-sh-social-portal.v260624.1",
        TargetProfileId = "rpa-sh-tax-etax",
        TargetSnapshotName = "rpa-sh-tax-etax.v260624.1",
        FirstTaskId = 123456,
        Timestamp = DateTimeOffset.Parse("2026-06-19T10:11:12+08:00")
    };
}

static RecordingGuestWorkerClient ReadyGuest(ActionRecorder recorder)
{
    return new RecordingGuestWorkerClient(recorder)
    {
        StatusResponses =
        [
            RunnerStatus("rpa-sh-tax-etax-001", "rpa-sh-social-portal", RunnerStatusCode.Runnable),
            RunnerStatus("rpa-sh-tax-etax-001", "rpa-sh-tax-etax", RunnerStatusCode.Runnable)
        ],
        KillResponse = KillSuccess()
    };
}

static RunnerStatusResponse RunnerStatus(string workerId, string profileId, RunnerStatusCode status, long? currentTaskId = null)
{
    return new RunnerStatusResponse
    {
        Success = true,
        WorkerId = workerId,
        ProfileId = profileId,
        RunnerStatusCode = status,
        RunnerStatusName = status.ToString(),
        CurrentTaskId = currentTaskId
    };
}

static KillRunnerResponse KillSuccess()
{
    return new KillRunnerResponse
    {
        Success = true,
        BeforeRunnerStatusCode = RunnerStatusCode.Runnable,
        AfterRunnerStatusCode = RunnerStatusCode.Closed,
        CurrentTaskId = null,
        LogFlushed = true
    };
}

static WorkerAgentOptions StartupOptions(string root)
{
    var vmrunPath = Path.Combine(root, "vmrun.exe");
    var vmxPath = Path.Combine(root, "SR20-2026-6HQ8.vmx");
    File.WriteAllText(vmrunPath, "");
    File.WriteAllText(vmxPath, "");

    return new WorkerAgentOptions
    {
        Agent = new AgentOptions
        {
            HostId = "HOST-SR20-001",
            AgentName = "SR20 Host Agent"
        },
        Vmrun = new VmrunOptions
        {
            VmrunPath = vmrunPath
        },
        VirtualMachines =
        [
            new VirtualMachineOptions
            {
                Name = "SR20-2026-6HQ8",
                VmxPath = vmxPath,
                BaseSnapshotName = "SR20-2026-6HQ8",
                WorkerId = "rpa-sh-tax-etax-001",
                Profiles =
                [
                    new ProfileOptions
                    {
                        ProfileId = "rpa-sh-tax-etax",
                        ProfileName = "Shanghai Tax",
                        SnapshotName = "rpa-sh-tax-etax.v260624.1"
                    },
                    new ProfileOptions
                    {
                        ProfileId = "rpa-sh-social-portal",
                        ProfileName = "Social Portal",
                        SnapshotName = "rpa-sh-social-portal.v260624.1"
                    }
                ]
            }
        ]
    };
}

static VirtualMachineOptions BackupVm(string hostWorkPath)
{
    return new VirtualMachineOptions
    {
        Name = "SR20-2026-6HQ8",
        VmxPath = @"D:\VMs\SR20-2026-6HQ8\SR20-2026-6HQ8.vmx",
        WorkerId = "rpa-sh-tax-etax-001",
        GuestUser = "Administrator",
        GuestPasswordSecret = "password",
        HostWorkPath = hostWorkPath,
        GuestWorkPath = @"C:\seebot",
        HostSharedPath = Path.Combine(hostWorkPath, "shared"),
        GuestSharedPath = @"C:\seebot",
        GuestBackupPaths = new GuestBackupPathsOptions
        {
            Cache = @"C:\seebot\cache",
            Db = @"C:\seebot\db",
            File = @"C:\seebot\file",
            Logs = @"C:\seebot\logs"
        }
    };
}

static void SeedBackupSource(string hostSharedPath)
{
    foreach (var directoryName in new[] { "cache", "db", "file", "logs" })
    {
        var directoryPath = Path.Combine(hostSharedPath, directoryName);
        Directory.CreateDirectory(directoryPath);
        File.WriteAllText(Path.Combine(directoryPath, $"{directoryName}.txt"), directoryName);
    }
}

static SwitchTransaction BackupTransaction()
{
    return new SwitchTransaction
    {
        TransactionId = "SWITCH-20260617-0001",
        HostId = "HOST-SR20-001",
        VmName = "SR20-2026-6HQ8",
        WorkerId = "rpa-sh-tax-etax-001",
        FromProfileId = "rpa-sh-social-portal",
        FromSnapshotName = "rpa-sh-social-portal",
        TargetProfileId = "rpa-sh-tax-etax",
        TargetSnapshotName = "rpa-sh-tax-etax",
        FirstTaskId = 123456,
        CreatedAt = DateTimeOffset.Parse("2026-06-19T10:00:00+08:00")
    };
}

static GuestWorkerClient NewGuestWorkerClient(FakeHttpMessageHandler handler)
{
    return new GuestWorkerClient(new HttpClient(handler));
}

static VirtualMachineOptions GuestVm()
{
    return new VirtualMachineOptions
    {
        Name = "SR20-2026-6HQ8",
        WorkerId = "rpa-sh-tax-etax-001",
        RunnerControlBaseUrl = "http://192.168.100.101:9090",
        RunnerStatusUrl = "http://192.168.100.101:9090/api/robot/start/status",
        RunnerKillUrl = "http://192.168.100.101:9090/api/robot/kill"
    };
}

static SchedulerClient NewSchedulerClient(FakeHttpMessageHandler handler)
{
    var httpClient = new HttpClient(handler)
    {
        BaseAddress = new Uri("http://seebot-server")
    };

    return new SchedulerClient(httpClient, new SchedulerOptions
    {
        BaseUrl = "http://seebot-server/api/rpa",
        AccessToken = "scheduler-token"
    });
}

static HttpContent JsonContent(string json)
{
    return new StringContent(json, Encoding.UTF8, "application/json");
}

static HttpResponseMessage OkResponse()
{
    return new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = JsonContent("""{"code":200,"message":"ok"}""")
    };
}

static VmrunService NewVmrunService(FakeProcessRunner runner)
{
    return new VmrunService(
        @"C:\Program Files (x86)\VMware\VMware Workstation\vmrun.exe",
        "ws",
        runner,
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60));
}

static VmrunCommandResult SuccessResult(string commandName)
{
    return new VmrunCommandResult(0, "", "", TimeSpan.FromMilliseconds(1), commandName);
}

static SwitchTransaction SampleTransaction(string txId, SwitchTransactionStatus status)
{
    var now = DateTimeOffset.Parse("2026-06-19T10:00:00+08:00");
    return new SwitchTransaction
    {
        TransactionId = txId,
        HostId = "SB-VM-001",
        VmName = "SR20-2026-6HQ8",
        WorkerId = "rpa-sh-tax-etax-01",
        FromProfileId = "rpa-sh-social-portal",
        FromSnapshotName = "rpa-sh-social-portal",
        TargetProfileId = "rpa-sh-tax-etax",
        TargetSnapshotName = "rpa-sh-tax-etax",
        FirstTaskId = 1001,
        TriggerReason = "pending-profile-task",
        Status = status,
        Step = "created",
        CreatedAt = now,
        UpdatedAt = now
    };
}

static bool SqliteTableExists(string databasePath, string tableName)
{
    using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
    command.Parameters.AddWithValue("$name", tableName);
    return Convert.ToInt32(command.ExecuteScalar()) == 1;
}

static TempDatabaseScope TempDatabase()
{
    var path = Path.Combine(Path.GetTempPath(), "rpa-worker-agent-tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    return new TempDatabaseScope(path);
}

static TempDirectoryScope TempDirectory()
{
    var path = Path.Combine(Path.GetTempPath(), "rpa-worker-agent-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return new TempDirectoryScope(path);
}

static WorkerAgentOptions ValidOptions()
{
    return new WorkerAgentOptions
    {
        Agent = new AgentOptions
        {
            HostId = "SB-VM-001",
            AgentName = "SR20 Host Agent",
            PollIntervalSeconds = 10,
            CapabilityReportIntervalSeconds = 300,
            SwitchTimeoutSeconds = 300,
            WaitVmReadyTimeoutSeconds = 180,
            WaitUpgradeTimeoutSeconds = 600,
            IdleStableSeconds = 30,
            ForceRevertWhenBackupFailed = false,
            AllowRevertWhenRunnerError = true,
            MaxSwitchesPerCycle = 1
        },
        OperationsApi = new OperationsApiOptions
        {
            ListenUrl = "http://127.0.0.1:18090",
            ApiKey = "local-secret"
        },
        Scheduler = new SchedulerOptions
        {
            BaseUrl = "http://seebot-server/api/rpa",
            AccessToken = "scheduler-token"
        },
        Vmrun = new VmrunOptions
        {
            VmrunPath = @"C:\Program Files (x86)\VMware\VMware Workstation\vmrun.exe",
            DefaultStartNoGui = true,
            StopSoftTimeoutSeconds = 60,
            AllowHardStopAfterSoftTimeout = true
        },
        VirtualMachines =
        [
            new VirtualMachineOptions
            {
                Name = "SR20-2026-6HQ8",
                VmxPath = @"D:\VMs\SR20-2026-6HQ8\SR20-2026-6HQ8.vmx",
                BaseSnapshotName = "SR20-2026-6HQ8",
                WorkerId = "rpa-sh-tax-etax-01",
                RunnerControlBaseUrl = "http://192.168.100.101:9090",
                RunnerStatusUrl = "http://192.168.100.101:9090/api/robot/start/status",
                RunnerKillUrl = "http://192.168.100.101:9090/api/robot/kill",
                HostWorkPath = @"D:\seebot",
                GuestBackupPaths = new GuestBackupPathsOptions
                {
                    Cache = @"C:\seebot\cache",
                    Db = @"C:\seebot\db",
                    File = @"C:\seebot\file",
                    Logs = @"C:\seebot\logs"
                },
                Profiles =
                [
                    new ProfileOptions
                    {
                        ProfileId = "rpa-sh-tax-etax",
                        ProfileName = "Shanghai Tax",
                        SnapshotName = "rpa-sh-tax-etax.v260624.1"
                    },
                    new ProfileOptions
                    {
                        ProfileId = "rpa-sh-social-portal",
                        ProfileName = "Social Portal",
                        SnapshotName = "rpa-sh-social-portal.v260624.1"
                    }
                ]
            },
            new VirtualMachineOptions
            {
                Name = "SR20-2026-7JK9",
                VmxPath = @"D:\VMs\SR20-2026-7JK9\SR20-2026-7JK9.vmx",
                BaseSnapshotName = "SR20-2026-7JK9",
                WorkerId = "rpa-sh-tax-etax-02",
                RunnerControlBaseUrl = "http://192.168.100.102:9090",
                RunnerStatusUrl = "http://192.168.100.102:9090/api/robot/start/status",
                RunnerKillUrl = "http://192.168.100.102:9090/api/robot/kill",
                HostWorkPath = @"D:\seebot",
                GuestBackupPaths = new GuestBackupPathsOptions
                {
                    Cache = @"C:\seebot\cache",
                    Db = @"C:\seebot\db",
                    File = @"C:\seebot\file",
                    Logs = @"C:\seebot\logs"
                },
                Profiles =
                [
                    new ProfileOptions
                    {
                        ProfileId = "rpa-sh-tax-etax",
                        ProfileName = "Shanghai Tax",
                        SnapshotName = "rpa-sh-tax-etax.v260624.1"
                    }
                ]
            }
        ]
    };
}

internal static class Assert
{
    public static void True(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void False(bool value, string message)
    {
        if (value)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void NotNull(object? value, string message)
    {
        if (value is null)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void NotEmpty<T>(IEnumerable<T> values, string message)
    {
        if (!values.Any())
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void Contains(IEnumerable<string> values, string expected)
    {
        if (!values.Any(value => value.Contains(expected, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Expected an error containing '{expected}'. Actual errors: {string.Join("; ", values)}");
        }
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; Actual: {actual}.");
        }
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException($"{message} Expected: [{string.Join(", ", expected)}]; Actual: [{string.Join(", ", actual)}].");
        }
    }

    public static TException Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name} was not thrown.");
    }
}

internal sealed class TempDatabaseScope : IDisposable
{
    public TempDatabaseScope(string databasePath)
    {
        DatabasePath = databasePath;
    }

    public string DatabasePath { get; }

    public void Dispose()
    {
        File.Delete(DatabasePath);
    }
}

internal sealed class TempDirectoryScope : IDisposable
{
    public TempDirectoryScope(string directoryPath)
    {
        DirectoryPath = directoryPath;
    }

    public string DirectoryPath { get; }

    public void Dispose()
    {
        if (Directory.Exists(DirectoryPath))
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}

internal sealed class FakeVmrunService : IVmrunService
{
    private readonly string? _failOnGuestPath;
    private readonly IReadOnlyList<string> _snapshots;

    public FakeVmrunService(string? failOnGuestPath = null, IReadOnlyList<string>? snapshots = null)
    {
        _failOnGuestPath = failOnGuestPath;
        _snapshots = snapshots ?? [];
    }

    public List<CopyCall> CopyCalls { get; } = [];

    public Task<IReadOnlyList<string>> ListSnapshotsAsync(string vmxPath, CancellationToken cancellationToken)
    {
        return Task.FromResult(_snapshots);
    }

    public Task<string?> GetCurrentSnapshotAsync(string vmxPath, CancellationToken cancellationToken)
    {
        return Task.FromResult<string?>(_snapshots.FirstOrDefault());
    }

    public Task<VmrunCommandResult> StopVmAsync(string vmxPath, VmStopMode mode, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<VmrunCommandResult> RevertToSnapshotAsync(string vmxPath, string snapshotName, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<VmrunCommandResult> StartVmAsync(string vmxPath, bool noGui, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<VmrunCommandResult> EnableSharedFoldersAsync(string vmxPath, CancellationToken cancellationToken)
    {
        return Task.FromResult(new VmrunCommandResult(0, "", "", TimeSpan.Zero, "enableSharedFolders"));
    }

    public Task<VmrunCommandResult> AddSharedFolderAsync(string vmxPath, string shareName, string hostPath, CancellationToken cancellationToken)
    {
        return Task.FromResult(new VmrunCommandResult(0, "", "", TimeSpan.Zero, "addSharedFolder"));
    }

    public Task<VmrunCommandResult> RemoveSharedFolderAsync(string vmxPath, string shareName, CancellationToken cancellationToken)
    {
        return Task.FromResult(new VmrunCommandResult(0, "", "", TimeSpan.Zero, "removeSharedFolder"));
    }

    public List<string> ProgramCalls { get; } = [];

    public Task<VmrunCommandResult> RunProgramInGuestAsync(
        string vmxPath,
        string guestUser,
        string guestPassword,
        string programPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProgramCalls.Add(string.Join(" ", arguments));
        return Task.FromResult(new VmrunCommandResult(0, "", "", TimeSpan.Zero, "runProgramInGuest"));
    }

    public Task<VmrunCommandResult> CopyFileFromHostToGuestAsync(
        string vmxPath,
        string guestUser,
        string guestPassword,
        string hostPath,
        string guestPath,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new VmrunCommandResult(0, "", "", TimeSpan.Zero, "copyFileFromHostToGuest"));
    }

    public Task<VmrunCommandResult> CopyFileFromGuestToHostAsync(
        string vmxPath,
        string guestUser,
        string guestPassword,
        string guestPath,
        string hostPath,
        CancellationToken cancellationToken)
    {
        CopyCalls.Add(new CopyCall(vmxPath, guestUser, guestPassword, guestPath, hostPath));
        if (string.Equals(guestPath, _failOnGuestPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("copy failed");
        }

        // 在 host 端创建占位文件，模拟 copy 完成
        if (!string.IsNullOrEmpty(hostPath))
        {
            var dir = Path.GetDirectoryName(hostPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(hostPath, []);
        }

        return Task.FromResult(new VmrunCommandResult(0, "", "", TimeSpan.Zero, "copyFileFromGuestToHost"));
    }

    public Task<VmrunCommandResult> CreateSnapshotAsync(string vmxPath, string snapshotName, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<VmrunCommandResult> DeleteSnapshotAsync(string vmxPath, string snapshotName, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }
}

internal sealed record CopyCall(
    string VmxPath,
    string GuestUser,
    string GuestPassword,
    string GuestPath,
    string HostPath);

internal sealed class ActionRecorder
{
    public List<string> Actions { get; } = [];
}

internal sealed class RecordingGuestWorkerClient : IGuestWorkerClient
{
    private readonly ActionRecorder _recorder;
    private readonly Queue<RunnerStatusResponse> _statusResponses = [];

    public RecordingGuestWorkerClient(ActionRecorder recorder)
    {
        _recorder = recorder;
    }

    public IEnumerable<RunnerStatusResponse> StatusResponses
    {
        set
        {
            _statusResponses.Clear();
            foreach (var response in value)
            {
                _statusResponses.Enqueue(response);
            }
        }
    }

    public KillRunnerResponse KillResponse { get; set; } = new()
    {
        Success = true,
        BeforeRunnerStatusCode = RunnerStatusCode.Runnable,
        AfterRunnerStatusCode = RunnerStatusCode.Closed,
        CurrentTaskId = null,
        LogFlushed = true
    };

    public Task<RunnerStatusResponse> GetRunnerStatusAsync(VirtualMachineOptions vm, CancellationToken cancellationToken)
    {
        _recorder.Actions.Add("get-status");
        return Task.FromResult(_statusResponses.Dequeue());
    }

    public Task<KillRunnerResponse> KillRunnerAsync(
        VirtualMachineOptions vm,
        string txId,
        string reason,
        int deadlineSeconds,
        CancellationToken cancellationToken)
    {
        _recorder.Actions.Add("kill");
        return Task.FromResult(KillResponse);
    }
}

internal sealed class RecordingBackupService : ILogBackupService
{
    private readonly ActionRecorder _recorder;

    public RecordingBackupService(ActionRecorder recorder)
    {
        _recorder = recorder;
    }

    public LogBackupResult Result { get; set; } = new()
    {
        Success = true,
        TargetPath = @"D:\backup",
        Directories = ["cache", "db", "file", "logs"]
    };

    public Task<LogBackupResult> BackupAsync(
        VirtualMachineOptions vm,
        SwitchTransaction transaction,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        _recorder.Actions.Add("backup");
        return Task.FromResult(Result);
    }
}

internal sealed class RecordingSwitchVmrunService : IVmrunService
{
    private readonly ActionRecorder _recorder;

    public RecordingSwitchVmrunService(ActionRecorder recorder)
    {
        _recorder = recorder;
    }

    public bool FailOnRevert { get; set; }

    public Task<IReadOnlyList<string>> ListSnapshotsAsync(string vmxPath, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<string?> GetCurrentSnapshotAsync(string vmxPath, CancellationToken cancellationToken)
    {
        return Task.FromResult<string?>(null);
    }

    public Task<VmrunCommandResult> StopVmAsync(string vmxPath, VmStopMode mode, CancellationToken cancellationToken)
    {
        _recorder.Actions.Add("vmrun-stop");
        return Task.FromResult(new VmrunCommandResult(0, "", "", TimeSpan.Zero, "stop"));
    }

    public Task<VmrunCommandResult> RevertToSnapshotAsync(string vmxPath, string snapshotName, CancellationToken cancellationToken)
    {
        _recorder.Actions.Add("vmrun-revert");
        if (FailOnRevert)
        {
            throw new InvalidOperationException("revert failed");
        }

        return Task.FromResult(new VmrunCommandResult(0, "", "", TimeSpan.Zero, "revertToSnapshot"));
    }

    public Task<VmrunCommandResult> StartVmAsync(string vmxPath, bool noGui, CancellationToken cancellationToken)
    {
        _recorder.Actions.Add("vmrun-start");
        return Task.FromResult(new VmrunCommandResult(0, "", "", TimeSpan.Zero, "start"));
    }

    public Task<VmrunCommandResult> EnableSharedFoldersAsync(string vmxPath, CancellationToken cancellationToken)
    {
        _recorder.Actions.Add("vmrun-enable-shared-folders");
        return Task.FromResult(new VmrunCommandResult(0, "", "", TimeSpan.Zero, "enableSharedFolders"));
    }

    public Task<VmrunCommandResult> AddSharedFolderAsync(string vmxPath, string shareName, string hostPath, CancellationToken cancellationToken)
    {
        _recorder.Actions.Add("vmrun-add-shared-folder");
        return Task.FromResult(new VmrunCommandResult(0, "", "", TimeSpan.Zero, "addSharedFolder"));
    }

    public Task<VmrunCommandResult> RemoveSharedFolderAsync(string vmxPath, string shareName, CancellationToken cancellationToken)
    {
        _recorder.Actions.Add("vmrun-remove-shared-folder");
        return Task.FromResult(new VmrunCommandResult(0, "", "", TimeSpan.Zero, "removeSharedFolder"));
    }

    public Task<VmrunCommandResult> CopyFileFromGuestToHostAsync(
        string vmxPath,
        string guestUser,
        string guestPassword,
        string guestPath,
        string hostPath,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<VmrunCommandResult> CreateSnapshotAsync(string vmxPath, string snapshotName, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<VmrunCommandResult> DeleteSnapshotAsync(string vmxPath, string snapshotName, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }
}

internal sealed class RecordingLocalStore : ILocalStore
{
    private readonly ActionRecorder _recorder;
    private readonly Dictionary<string, SwitchTransaction> _transactions = [];

    public RecordingLocalStore(ActionRecorder recorder)
    {
        _recorder = recorder;
    }

    public IReadOnlyList<VmCurrentState> VmStates { get; set; } = [];

    public List<VmCurrentState> UpsertedVmStates { get; } = [];

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task SeedVmStatesAsync(string hostId, IReadOnlyList<VmCurrentState> initialStates, CancellationToken cancellationToken = default)
    {
        VmStates = initialStates;
        return Task.CompletedTask;
    }

    public Task UpsertVmStateAsync(string hostId, VmCurrentState state, CancellationToken cancellationToken = default)
    {
        UpsertedVmStates.Add(state);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VmCurrentState>> GetVmStatesAsync(string hostId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(VmStates);
    }

    public Task<VmCurrentState?> GetVmStateAsync(string hostId, string vmName, CancellationToken cancellationToken = default)
    {
        var state = VmStates.FirstOrDefault(item => string.Equals(item.VmName, vmName, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(state);
    }

    public Task CreateSwitchTransactionAsync(SwitchTransaction transaction, CancellationToken cancellationToken = default)
    {
        _recorder.Actions.Add("create-tx");
        _transactions[transaction.TransactionId] = transaction;
        return Task.CompletedTask;
    }

    public Task UpdateSwitchTransactionAsync(
        string txId,
        SwitchTransactionStatus status,
        string? step,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        _recorder.Actions.Add($"update:{status}");
        if (_transactions.TryGetValue(txId, out var transaction))
        {
            transaction.Status = status;
            transaction.Step = step;
            transaction.ErrorCode = errorCode;
            transaction.ErrorMessage = errorMessage;
            transaction.UpdatedAt = updatedAt;
            if (status is SwitchTransactionStatus.SUCCESS or SwitchTransactionStatus.FAILED)
            {
                transaction.CompletedAt = updatedAt;
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SwitchTransaction>> GetIncompleteSwitchTransactionsAsync(string hostId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<SwitchTransaction>>([]);
    }

    public Task DeleteSwitchTransactionAsync(string txId, CancellationToken cancellationToken = default)
    {
        _transactions.Remove(txId);
        return Task.CompletedTask;
    }

    public Task<SwitchTransaction?> GetSwitchTransactionAsync(string txId, CancellationToken cancellationToken = default)
    {
        _transactions.TryGetValue(txId, out var transaction);
        return Task.FromResult(transaction);
    }
}

internal sealed class RecordingSchedulerClient : ISchedulerClient
{
    public Dictionary<string, ProfilePendingTaskResponse> Pending { get; } = [];

    public List<string> QueriedProfileIds { get; } = [];

    public List<IReadOnlyList<HostProfileCapabilityRequest>> ReportedCapabilities { get; } = [];

    public List<VmStatusReportRequest> ReportedVmStatuses { get; } = [];

    public bool ThrowOnVmStatusReport { get; set; }

    public Task<IReadOnlyList<ProfilePendingTaskResponse>> QueryPendingTasksAsync(string workerId, CancellationToken cancellationToken)
    {
        QueriedProfileIds.Add(workerId);
        return Task.FromResult<IReadOnlyList<ProfilePendingTaskResponse>>(Pending.Values.ToList());
    }

    public Task ReportCapabilitiesAsync(IReadOnlyList<HostProfileCapabilityRequest> request, CancellationToken cancellationToken)
    {
        ReportedCapabilities.Add(request);
        return Task.CompletedTask;
    }

    public Task ReportVmStatusAsync(VmStatusReportRequest request, CancellationToken cancellationToken)
    {
        if (ThrowOnVmStatusReport)
        {
            throw new InvalidOperationException("vm status failed");
        }

        ReportedVmStatuses.Add(request);
        return Task.CompletedTask;
    }

    public Task ReportSwitchLogAsync(WorkerSwitchLogRequest request, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task ReportDirectoryBackupResultAsync(DirectoryBackupResultRequest request, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }
}

internal sealed class RecordingStartupValidator : IStartupValidator
{
    private readonly WorkerAgentOptions _options;

    public RecordingStartupValidator(WorkerAgentOptions options)
    {
        _options = options;
    }

    public Task<StartupValidationResult> ValidateAndBuildCapabilitiesAsync(
        WorkerAgentOptions options,
        string reportedAt,
        CancellationToken cancellationToken)
    {
        var capabilities = new HostAgentCapabilitiesRequest
        {
            HostId = _options.Agent.HostId,
            AgentName = _options.Agent.AgentName,
            ReportedAt = reportedAt,
            Vms = _options.VirtualMachines.Select(vm => new VmCapabilityDto
            {
                VmName = vm.Name,
                WorkerId = vm.WorkerId,
                VmxPath = vm.VmxPath,
                BaseSnapshotName = vm.BaseSnapshotName,
                Enabled = true,
                Profiles = vm.Profiles.Select(profile => new ProfileCapabilityDto
                {
                    ProfileId = profile.ProfileId,
                    SnapshotName = profile.SnapshotName,
                    Enabled = true,
                    SnapshotExists = true,
                    ValidationStatus = "READY"
                }).ToList()
            }).ToList()
        };

        return Task.FromResult(new StartupValidationResult(capabilities, []));
    }
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Messages.Add(formatter(state, exception));
    }
}

internal sealed class RecordingSnapshotUpdateVmrunService : IVmrunService
{
    private readonly ActionRecorder _recorder;
    private readonly IReadOnlyList<string> _nextSnapshots;

    public RecordingSnapshotUpdateVmrunService(ActionRecorder recorder, IReadOnlyList<string> nextSnapshots)
    {
        _recorder = recorder;
        _nextSnapshots = nextSnapshots;
    }

    public bool FailOnRevert { get; set; }

    public Task<IReadOnlyList<string>> ListSnapshotsAsync(string vmxPath, CancellationToken cancellationToken)
    {
        _recorder.Actions.Add("list-snapshots");
        return Task.FromResult(_nextSnapshots);
    }

    public Task<string?> GetCurrentSnapshotAsync(string vmxPath, CancellationToken cancellationToken)
    {
        return Task.FromResult<string?>(_nextSnapshots.FirstOrDefault());
    }

    public Task<VmrunCommandResult> StopVmAsync(string vmxPath, VmStopMode mode, CancellationToken cancellationToken)
    {
        _recorder.Actions.Add("vmrun-stop");
        return Task.FromResult(new VmrunCommandResult(0, "", "", TimeSpan.Zero, "stop"));
    }

    public Task<VmrunCommandResult> RevertToSnapshotAsync(string vmxPath, string snapshotName, CancellationToken cancellationToken)
    {
        _recorder.Actions.Add("vmrun-revert");
        if (FailOnRevert)
        {
            throw new InvalidOperationException("revert failed");
        }

        return Task.FromResult(new VmrunCommandResult(0, "", "", TimeSpan.Zero, "revertToSnapshot"));
    }

    public Task<VmrunCommandResult> StartVmAsync(string vmxPath, bool noGui, CancellationToken cancellationToken)
    {
        _recorder.Actions.Add("vmrun-start");
        return Task.FromResult(new VmrunCommandResult(0, "", "", TimeSpan.Zero, "start"));
    }

    public Task<VmrunCommandResult> EnableSharedFoldersAsync(string vmxPath, CancellationToken cancellationToken)
    {
        _recorder.Actions.Add("vmrun-enable-shared-folders");
        return Task.FromResult(new VmrunCommandResult(0, "", "", TimeSpan.Zero, "enableSharedFolders"));
    }

    public Task<VmrunCommandResult> AddSharedFolderAsync(string vmxPath, string shareName, string hostPath, CancellationToken cancellationToken)
    {
        _recorder.Actions.Add("vmrun-add-shared-folder");
        return Task.FromResult(new VmrunCommandResult(0, "", "", TimeSpan.Zero, "addSharedFolder"));
    }

    public Task<VmrunCommandResult> RemoveSharedFolderAsync(string vmxPath, string shareName, CancellationToken cancellationToken)
    {
        _recorder.Actions.Add("vmrun-remove-shared-folder");
        return Task.FromResult(new VmrunCommandResult(0, "", "", TimeSpan.Zero, "removeSharedFolder"));
    }

    public Task<VmrunCommandResult> CopyFileFromGuestToHostAsync(
        string vmxPath, string guestUser, string guestPassword,
        string guestPath, string hostPath, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<VmrunCommandResult> CreateSnapshotAsync(string vmxPath, string snapshotName, CancellationToken cancellationToken)
    {
        _recorder.Actions.Add("vmrun-create");
        return Task.FromResult(new VmrunCommandResult(0, "", "", TimeSpan.Zero, "snapshot"));
    }

    public Task<VmrunCommandResult> DeleteSnapshotAsync(string vmxPath, string snapshotName, CancellationToken cancellationToken)
    {
        _recorder.Actions.Add("vmrun-delete");
        return Task.FromResult(new VmrunCommandResult(0, "", "", TimeSpan.Zero, "deleteSnapshot"));
    }
}

internal sealed class RecordingConfigFileUpdater : IConfigFileUpdater
{
    private readonly ActionRecorder _recorder;

    public RecordingConfigFileUpdater(ActionRecorder recorder)
    {
        _recorder = recorder;
    }

    public Task UpdateSnapshotNameAsync(string vmName, string profileId, string newSnapshotName, CancellationToken cancellationToken)
    {
        _recorder.Actions.Add("config-update");
        return Task.CompletedTask;
    }
}

internal sealed class RecordingVmStateRefreshService : IVmStateRefreshService
{
    public Task RefreshAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

internal sealed class RecordingVmSwitchService : IVmSwitchService
{
    public List<VmSwitchRequest> Requests { get; } = [];

    public Task<VmSwitchResult> SwitchAsync(VmSwitchRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(new VmSwitchResult
        {
            TxId = request.TxId ?? "",
            Success = true
        });
    }
}

internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _utcNow;

    public FixedTimeProvider(DateTimeOffset now)
    {
        _utcNow = now.ToUniversalTime();
    }

    public override DateTimeOffset GetUtcNow()
    {
        return _utcNow;
    }
}

internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Queue<VmrunCommandResult> _results;

    public FakeProcessRunner(params VmrunCommandResult[] results)
    {
        _results = new Queue<VmrunCommandResult>(results);
    }

    public ProcessCommand? LastCommand { get; private set; }

    public Task<VmrunCommandResult> RunAsync(ProcessCommand command, CancellationToken cancellationToken)
    {
        LastCommand = command;
        return Task.FromResult(_results.Count == 0
            ? new VmrunCommandResult(0, "", "", TimeSpan.Zero, command.CommandName)
            : _results.Dequeue());
    }
}

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses;
    private readonly Exception? _exception;

    public FakeHttpMessageHandler(params HttpResponseMessage[] responses)
    {
        _responses = new Queue<HttpResponseMessage>(responses);
    }

    public FakeHttpMessageHandler(Exception exception)
    {
        _responses = new Queue<HttpResponseMessage>();
        _exception = exception;
    }

    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    public JsonDocument LastJsonDocument()
    {
        if (LastRequestBody is null)
        {
            throw new InvalidOperationException("No request body was captured.");
        }

        return JsonDocument.Parse(LastRequestBody);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        if (_exception is not null)
        {
            throw _exception;
        }

        return _responses.Count == 0 ? new HttpResponseMessage(HttpStatusCode.OK) : _responses.Dequeue();
    }
}
