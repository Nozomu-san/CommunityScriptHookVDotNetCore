using CommunityScriptHookVDotNetCore.Source;
using System.Diagnostics;

namespace LocalNativeMemories.Source;

internal sealed class PoolAdmission
{
    private const int BatchCount = 4;
    private static readonly long DecisionDeadlineTicks = 7L * Stopwatch.Frequency / 4L;
    private static readonly long BatchIntervalTicks = Stopwatch.Frequency / 5L;
    private static readonly LocalPoolKinds[] PoolOrder =
    [
        LocalPoolKinds.Peds,
        LocalPoolKinds.Vehicles,
        LocalPoolKinds.Objects
    ];

    private readonly EntityPools _pools;
    private readonly IRuntimeInformationSink _information;
    private long _readyStartedAt;
    private long _lastSampleAt;
    private long _lastAdvanceAt;
    private ulong _lastAdvanceFrame;
    private ulong _advanceCount;
    private readonly PoolAdmissionEvidence[,] _samples = new PoolAdmissionEvidence[BatchCount, 3];
    private readonly long[] _sampleStartedAt = new long[BatchCount];
    private int _collected;
    private int _missed;
    private bool _admitted;

    internal PoolAdmission(EntityPools pools, IRuntimeInformationSink information)
    {
        _pools = pools ?? throw new ArgumentNullException(nameof(pools));
        _information = information ?? throw new ArgumentNullException(nameof(information));
    }

    private static double ToMilliseconds(long ticks) =>
        (double)Math.Max(0L, ticks) * 1000d / Stopwatch.Frequency;

    internal bool Advance(ulong hostFrameIndex)
    {
        if (_admitted)
        {
            return true;
        }

        long now = Stopwatch.GetTimestamp();
        long callGap = _lastAdvanceAt == 0
            ? 0L : Math.Max(0L, now - _lastAdvanceAt);
        ulong frameGap = _advanceCount != 0 &&
            hostFrameIndex >= _lastAdvanceFrame
            ? hostFrameIndex - _lastAdvanceFrame : 0UL;
        ++_advanceCount;
        _lastAdvanceAt = now;
        _lastAdvanceFrame = hostFrameIndex;
        if (_readyStartedAt == 0)
        {
            _readyStartedAt = now;
            _information.Information("Checking memory layout compatibility...");
            _information.Information(
                "LNM admission implementation=v5r11 policy=per-batch " +
                "churn=diagnostic-only requiredBatches=4 " +
                "fallback=diagnostic-only");
            _information.Information(
                $"LNM admission clock-start frame={hostFrameIndex} " +
                $"deadlineMs={ToMilliseconds(DecisionDeadlineTicks):F1} " +
                $"sampleIntervalMs={ToMilliseconds(BatchIntervalTicks):F1} " +
                $"requiredBatches={BatchCount}.");
        }

        long elapsed = Math.Max(0L, now - _readyStartedAt);
        if (_advanceCount > 1u && callGap >= BatchIntervalTicks * 2L)
        {
            _information.Information(
                $"LNM admission advance-gap frame={hostFrameIndex} " +
                $"frameDelta={frameGap} " +
                $"elapsedMs={ToMilliseconds(elapsed):F1} " +
                $"sincePreviousAdvanceMs={ToMilliseconds(callGap):F1} " +
                $"completedBatches={_collected}/{BatchCount}.");
        }
        if (_collected < BatchCount && elapsed < DecisionDeadlineTicks &&
            (_collected == 0 || now - _lastSampleAt >= BatchIntervalTicks))
        {
            long captureStartedAt = Stopwatch.GetTimestamp();
            _sampleStartedAt[_collected] = captureStartedAt;
            CaptureBatch();
            long captureEndedAt = Stopwatch.GetTimestamp();
            _lastSampleAt = now;
            long nextEligibleAt = _lastSampleAt + BatchIntervalTicks;
            _information.Information(
                $"LNM admission timing batch={_collected}/{BatchCount} " +
                $"frame={hostFrameIndex} advanceCalls={_advanceCount} " +
                $"captureMs={ToMilliseconds(captureEndedAt - captureStartedAt):F1} " +
                $"elapsedAtEndMs={ToMilliseconds(captureEndedAt - _readyStartedAt):F1} " +
                $"nextSampleEligibleMs={ToMilliseconds(nextEligibleAt - _readyStartedAt):F1} " +
                $"remainingDeadlineMs={ToMilliseconds(DecisionDeadlineTicks - (captureEndedAt - _readyStartedAt)):F1}.");
        }

        if (_collected == BatchCount && _missed == 0)
        {
            Evaluate();
            _pools.GrantAdmission();
            _information.Information(
                $"LNM admission decision=PASS batches={_collected}/{BatchCount} " +
                $"frame={hostFrameIndex} advanceCalls={_advanceCount} " +
                $"elapsedMs={ToMilliseconds(Stopwatch.GetTimestamp() - _readyStartedAt):F1} " +
                "all pools verified; GUID layout is now ready for modding.");
            _admitted = true;
            return true;
        }

        if (elapsed >= DecisionDeadlineTicks)
        {
            _information.Information(
                $"LNM admission decision=FAIL completeBatches={_collected - _missed}/{BatchCount} " +
                $"failedBatches={_missed} elapsedMs={ToMilliseconds(elapsed):F1} " +
                $"frame={hostFrameIndex} advanceCalls={_advanceCount}; " +
                "required observations were not all certified.");
            throw new InvalidOperationException(
                $"LNM memory layout admission is incomplete: " +
                $"valid batches={_collected}/{BatchCount}, failures={_missed}. " +
                "The layout is not certified for this runtime session.");
        }

        return false;
    }

    private void CaptureBatch()
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
            bool valid = directValid && enumeratedValid && guidValid;
            _information.Information(
                $"LNM admission batch={batch}/{BatchCount} pool={kind} " +
                $"result={(valid ? "PASS" : "FAIL")} probeMs={probeMs:F1} " +
                $"capacity={capacity} directActive={active} " +
                $"enumerated={enumerated} gap={difference} " +
                $"countsEqual={(difference == 0)} " +
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
        else
        {
            ++_missed;
        }
        _information.Information(
            $"LNM admission batch={batch}/{BatchCount} " +
            $"result={(complete ? "PASS" : "FAIL")} " +
            $"failedBatches={_missed}; only complete batches are eligible for certification.");
        ++_collected;
    }

    private void Evaluate()
    {
        string? firstFailure = null;
        bool lateSampling = false;
        for (int index = 1; index < BatchCount; ++index)
        {
            long interval = _sampleStartedAt[index] - _sampleStartedAt[index - 1];
            lateSampling |= interval > BatchIntervalTicks * 2L;
        }

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

                int tolerance = (int)Math.Max(2L,
                    Math.Min(8L, (maximumInBatch + 49L) / 50L));
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
                $"lateSampling={lateSampling} " +
                $"meanFallback={(lateSampling ? "diagnostic-only" : "not-needed")} " +
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