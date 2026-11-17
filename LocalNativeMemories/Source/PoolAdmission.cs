using CommunityScriptHookVDotNetCore.Source;
using System.Diagnostics;

namespace LocalNativeMemories.Source;

internal sealed class PoolAdmission
{
    private const int BatchCount = 4;
    private const int MaximumCountGap = 2;
    private static readonly long RoundDeadlineTicks = 2L * Stopwatch.Frequency;
    private static readonly LocalPoolKinds[] PoolOrder =
    [
        LocalPoolKinds.Peds,
        LocalPoolKinds.Vehicles,
        LocalPoolKinds.Objects
    ];

    private readonly EntityPools _pools;
    private readonly IRuntimeInformationSink _information;
    private readonly IRuntimeDiagnosticSink _notifications;
    private long _readyStartedAt;
    private ulong _advanceCount;
    private readonly PoolAdmissionEvidence[,] _samples = new PoolAdmissionEvidence[BatchCount, 3];
    private int _collected;
    private bool _admitted;

    internal PoolAdmission(
        EntityPools pools,
        IRuntimeInformationSink information,
        IRuntimeDiagnosticSink notifications)
    {
        _pools = pools ?? throw new ArgumentNullException(nameof(pools));
        _information = information ?? throw new ArgumentNullException(nameof(information));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
    }

    private static double ToMilliseconds(long ticks) =>
        (double)Math.Max(0L, ticks) * 1000d / Stopwatch.Frequency;

    internal bool Advance(ulong hostFrameIndex)
    {
        if (_admitted)
        {
            return true;
        }

        ++_advanceCount;
        if (_readyStartedAt == 0)
        {
            _readyStartedAt = Stopwatch.GetTimestamp();
            _information.Information("Checking memory layout compatibility...");
            _notifications.Notify(
                "LocalNativeMemories",
                "Checking game memory layout compatibility...",
                RuntimeUserNotificationColor.Blue);
            _information.Information(
                $"LNM admission clock-start frame={hostFrameIndex} " +
                $"roundDeadlineMs={ToMilliseconds(RoundDeadlineTicks):F1} " +
                $"requiredRounds={BatchCount} maximumCountGap={MaximumCountGap}.");
        }

        while (_collected < BatchCount)
        {
            int round = _collected + 1;
            long startedAt = Stopwatch.GetTimestamp();
            bool passed = CaptureBatch();
            long elapsed = Stopwatch.GetTimestamp() - startedAt;
            if (elapsed >= RoundDeadlineTicks)
            {
                _information.Information(
                    $"LNM admission decision=FAIL round={round}/{BatchCount} " +
                    $"reason=timeout elapsedMs={ToMilliseconds(elapsed):F1} " +
                    $"deadlineMs={ToMilliseconds(RoundDeadlineTicks):F1}.");
                throw new InvalidOperationException(
                    $"LNM memory layout admission timed out during round {round}/{BatchCount}.");
            }
            if (!passed)
            {
                _information.Information(
                    $"LNM admission decision=FAIL round={round}/{BatchCount} " +
                    "reason=incompatible-pool-evidence.");
                throw new InvalidOperationException(
                    $"LNM memory layout admission failed during round {round}/{BatchCount}.");
            }

            ++_collected;
            _information.Information(
                $"LNM admission round={round}/{BatchCount} result=PASS " +
                $"elapsedMs={ToMilliseconds(elapsed):F1} " +
                $"remainingDeadlineMs={ToMilliseconds(RoundDeadlineTicks - elapsed):F1}.");
        }

        Evaluate();
        _pools.GrantAdmission();
        _information.Information(
            $"LNM admission decision=PASS rounds={_collected}/{BatchCount} " +
            $"frame={hostFrameIndex} advanceCalls={_advanceCount} " +
            $"elapsedMs={ToMilliseconds(Stopwatch.GetTimestamp() - _readyStartedAt):F1} " +
            "all pools verified; GUID layout is now ready for modding.");
        _admitted = true;
        _notifications.Notify(
            "LocalNativeMemories",
            "Memory compatibility verified. LocalNativeMemories is ready.",
            RuntimeUserNotificationColor.Green);
        return true;
    }

    private bool CaptureBatch()
    {
        int batch = _collected + 1;
        PoolAdmissionEvidence[] current = new PoolAdmissionEvidence[PoolOrder.Length];
        bool complete = true;
        for (int kindIndex = 0; kindIndex < PoolOrder.Length; ++kindIndex)
        {
            LocalPoolKinds kind = PoolOrder[kindIndex];
            long probeStartedAt = Stopwatch.GetTimestamp();
            LocalDataResult<PoolAdmissionEvidence> result = _pools.VerifyAdmissionPool(
                kind, out string probeDetails);
            double probeMs = ToMilliseconds(
                Stopwatch.GetTimestamp() - probeStartedAt);
            if (!result.IsSuccess)
            {
                _information.Information(
                    $"LNM admission batch={batch}/{BatchCount} pool={kind} " +
                    $"result=FAIL stage=probe status={result.Status} " +
                    $"probeMs={probeMs:F1}; " +
                    $"direct/enumerated comparison unavailable; {probeDetails}.");
                complete = false;
                continue;
            }

            PoolAdmissionEvidence sample = result.Value;
            int capacity = sample.Direct.Capacity;
            int active = sample.Direct.ActiveCount;
            int enumerated = sample.EnumeratedCount;
            long difference = Math.Abs((long)active - enumerated);
            bool directValid = sample.Direct.IsConsistent;
            bool enumeratedValid = enumerated >= 0 && enumerated <= capacity;
            bool guidValid = enumerated == 0 || sample.GuidVerified;
            bool valid = difference <= MaximumCountGap &&
                directValid && enumeratedValid && guidValid;
            _information.Information(
                $"LNM admission batch={batch}/{BatchCount} pool={kind} " +
                $"result={(valid ? "PASS" : "FAIL")} probeMs={probeMs:F1} " +
                $"capacity={capacity} directActive={active} " +
                $"enumerated={enumerated} gap={difference} " +
                $"countsEqual={(difference == 0)} maximumCountGap={MaximumCountGap} " +
                $"directValid={directValid} enumeratedInRange={enumeratedValid} " +
                $"guidVerified={sample.GuidVerified} guidRequired={enumerated != 0} " +
                $"guidValid={guidValid}; {probeDetails}.");
            if (!valid)
            {
                complete = false;
                continue;
            }
            current[kindIndex] = sample;
        }

        if (complete)
        {
            for (int kindIndex = 0; kindIndex < PoolOrder.Length; ++kindIndex)
            {
                _samples[_collected, kindIndex] = current[kindIndex];
            }
        }
        _information.Information(
            $"LNM admission batch={batch}/{BatchCount} " +
            $"result={(complete ? "PASS" : "FAIL")}; " +
            "only complete batches are eligible for certification.");
        return complete;
    }

    private void Evaluate()
    {
        string? firstFailure = null;
        for (int kindIndex = 0; kindIndex < PoolOrder.Length; ++kindIndex)
        {
            LocalPoolKinds kind = PoolOrder[kindIndex];
            long totalGap = 0;
            long totalDirectMovement = 0;
            long totalEnumeratedMovement = 0;
            long maximumGap = 0;
            int maximum = 0;
            bool allBatchesPass = true;
            bool hasGuidEvidence = false;
            int firstFailedBatch = 0;

            for (int sampleIndex = 0; sampleIndex < BatchCount; ++sampleIndex)
            {
                PoolAdmissionEvidence sample = _samples[sampleIndex, kindIndex];
                long directCount = sample.Direct.ActiveCount;
                long enumeratedCount = sample.EnumeratedCount;
                int maximumInBatch = (int)Math.Max(directCount, enumeratedCount);
                maximum = Math.Max(maximum, maximumInBatch);
                long gap = Math.Abs(directCount - enumeratedCount);
                maximumGap = Math.Max(maximumGap, gap);
                totalGap += gap;
                hasGuidEvidence |= sample.GuidVerified;

                int tolerance = MaximumCountGap;
                bool directValid = sample.Direct.IsConsistent;
                bool enumeratedValid = enumeratedCount >= 0 &&
                    enumeratedCount <= sample.Direct.Capacity;
                bool guidValid = enumeratedCount == 0 || sample.GuidVerified;
                bool pass = gap <= tolerance && directValid &&
                    enumeratedValid && guidValid;
                allBatchesPass &= pass;
                if (!pass && firstFailedBatch == 0)
                {
                    firstFailedBatch = sampleIndex + 1;
                }
                _information.Information(
                    $"LNM admission compare batch={sampleIndex + 1}/{BatchCount} " +
                    $"pool={kind} mode=per-batch result={(pass ? "PASS" : "FAIL")} " +
                    $"direct={directCount} enumerated={enumeratedCount} " +
                    $"gap={gap} tolerance={tolerance} " +
                    $"countsEqual={gap == 0} guidValid={guidValid}.");

                if (sampleIndex == 0)
                {
                    continue;
                }
                PoolAdmissionEvidence previous = _samples[sampleIndex - 1, kindIndex];
                long directDelta = directCount - previous.Direct.ActiveCount;
                long enumeratedDelta = enumeratedCount - previous.EnumeratedCount;
                totalDirectMovement += Math.Abs(directDelta);
                totalEnumeratedMovement += Math.Abs(enumeratedDelta);
                _information.Information(
                    $"LNM admission movement={sampleIndex}/{BatchCount - 1} " +
                    $"pool={kind} directDelta={directDelta} " +
                    $"enumeratedDelta={enumeratedDelta} " +
                    $"movementDifference={Math.Abs(directDelta - enumeratedDelta)} " +
                    "decision=diagnostic-only.");
            }

            bool guidEvidencePass = maximum == 0 || hasGuidEvidence;
            bool passed = allBatchesPass && guidEvidencePass;
            double meanGap = (double)totalGap / BatchCount;
            double meanDirectMovement = (double)totalDirectMovement / (BatchCount - 1);
            double meanEnumeratedMovement = (double)totalEnumeratedMovement / (BatchCount - 1);
            _information.Information(
                $"LNM admission evaluation pool={kind} " +
                $"result={(passed ? "PASS" : "FAIL")} mode=per-batch " +
                $"batchAgreementPass={allBatchesPass} maxGap={maximumGap} " +
                $"firstFailedBatch={firstFailedBatch} " +
                $"guidEvidence={hasGuidEvidence} guidEvidencePass={guidEvidencePass} " +
                $"meanGap={meanGap:F2} meanDirectChange={meanDirectMovement:F2} " +
                $"meanEnumeratedChange={meanEnumeratedMovement:F2}.");
            if (!passed && firstFailure is null)
            {
                firstFailure =
                    $"LNM memory layout mismatch for {kind}: " +
                    $"firstFailedBatch={firstFailedBatch}, maxGap={maximumGap}, " +
                    $"guidVerified={hasGuidEvidence}. " +
                    "Every captured batch must independently pass Direct/Enumerated/GUID verification.";
            }
        }

        if (firstFailure is not null)
        {
            throw new InvalidOperationException(firstFailure);
        }
    }
}