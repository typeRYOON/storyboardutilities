/* Utilities.cs */
using StorybrewCommon.Storyboarding;
using StorybrewCommon.Storyboarding.CommandValues;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using StorybrewCommon.Scripting;
using System.Runtime.CompilerServices;
using OpenTK;
using System.Collections.Concurrent;
using StorybrewCommon.Animations;

[assembly: SupportedOSPlatform("windows7.0")]
namespace StoryboardUtilities {

/// <summary>
/// Pools one sprite path across a section so a sprite that has finished can be handed out
/// again instead of a new one being created. Reservations are tracked per
/// <see cref="OsbOrigin"/>, because a sprite origin is fixed once the sprite exists.
/// </summary>
/// <remarks>
/// First-fit reuse strategy, per-origin resource pools.
/// Allocation: O(S * log(T)). Insert: O(T) (list shift).
/// Could be implemented with an interval tree.
/// </remarks>
public sealed class SpriteAllocator(
    StoryboardLayer lyr,
    String spr_path,
    String allocator_name = "Allocator()")
{
    // A class, not a struct: foreach over List<T> yields copies, and the reuse logic
    // mutates Timeline through the reference. As a struct that only works by accident,
    // and any value field added later would silently fail to persist.
    private sealed class SpriteUsage(OsbSprite spr, SectionTime interval)
    {
        public OsbSprite Sprite { get; } = spr;
        public List<SectionTime> Timeline { get; } = [interval];
    }

    private readonly Dictionary<OsbOrigin, List<SpriteUsage>> _spritesByOrigin = [];
    private readonly StoryboardLayer _layer = lyr;
    private readonly String _path = spr_path;
    private readonly String _allocatorName = allocator_name;
    private bool _lastSpriteNew = false;

    /// <summary>
    /// Releases every reservation in the <paramref name="origin"/> pool overlapping
    /// <paramref name="interval"/>, making those sprites available in that range again.
    /// Pass null to release the whole pool.
    /// </summary>
    public void Clear(OsbOrigin origin, SectionTime? interval = null)
    {
        if (_spritesByOrigin.TryGetValue(origin, out List<SpriteUsage>? list))
            ClearInterval(list, interval);
    }

    /// <summary>
    /// <see cref="Clear(OsbOrigin, SectionTime?)"/> applied to every origin pool.
    /// </summary>
    public void Clear(SectionTime? interval = null)
    {
        foreach (List<SpriteUsage> list in _spritesByOrigin.Values)
            ClearInterval(list, interval);
    }

    // Drops every timeline entry overlapping `interval`, or all of them when it is null.
    // A sprite whose timeline empties is kept: it already exists on the layer, and an
    // empty timeline just means it is free for the whole song.
    private static void ClearInterval(List<SpriteUsage> list, SectionTime? interval)
    {
        foreach (SpriteUsage su in list)
        {
            if (interval == null)
            {
                su.Timeline.Clear();
                continue;
            }

            SectionTime t = interval.Value;
            Int32 idx = FindInsertIndex(su.Timeline, t);
            // The entry before the insert point can still reach into the interval.
            if (idx > 0 && su.Timeline[idx - 1].Et > t.St)
                --idx;

            while (idx < su.Timeline.Count && su.Timeline[idx].St < t.Et)
            {
                if (su.Timeline[idx].Et > t.St)
                    su.Timeline.RemoveAt(idx);
                else
                    ++idx;
            }
        }
    }

    /// <summary>
    /// How many pooled sprites are reusable during <paramref name="t"/>, across every origin.
    /// </summary>
    public Int32 FreeSprites(SectionTime t)
    {
        Int32 count = 0;
        foreach (OsbOrigin origin in _spritesByOrigin.Keys)
            count += FreeSprites(t, origin);
        return count;
    }

    /// <summary>
    /// How many pooled sprites of <paramref name="origin"/> are reusable during <paramref name="t"/>.
    /// </summary>
    public Int32 FreeSprites(SectionTime t, OsbOrigin origin)
    {
        Int32 count = 0;
        if (!_spritesByOrigin.TryGetValue(origin, out List<SpriteUsage>? list))
            return count;
        foreach (SpriteUsage su in list)
        {
            if (CanReuse(su.Timeline, t))
                count++;
        }
        return count;
    }

    /// <summary>Total sprites this allocator has created, across every origin.</summary>
    public Int32 SpriteCount()
    {
        Int32 count = 0;
        foreach (List<SpriteUsage> x in _spritesByOrigin.Values)
            count += x.Count;
        return count;
    }

    /// <summary>Sprites this allocator has created with <paramref name="origin"/>.</summary>
    public Int32 SpriteCount(OsbOrigin origin)
    {
        if (_spritesByOrigin.TryGetValue(origin, out List<SpriteUsage>? list))
            return list.Count;
        return 0;
    }

    public override String ToString()
    {
        List<String> lines = [];
        foreach ((OsbOrigin origin, List<SpriteUsage> list) in _spritesByOrigin)
        {
            lines.Add($"[{_allocatorName}]: OsbOrigin.{origin}");
            Int32 sprite_i = 0;
            foreach (SpriteUsage s in list)
            {
                lines.Add($"    Sprite {sprite_i++}:");
                foreach (SectionTime t in s.Timeline)
                    lines.Add($"        [{t}]");
            }
        }
        return String.Join("\n", lines);
    }

    public String ToString(OsbOrigin origin)
    {
        List<String> lines = [];
        lines.Add($"[{_allocatorName}]: OsbOrigin.{origin}");
        if (!_spritesByOrigin.TryGetValue(origin, out List<SpriteUsage>? list))
            return "";
        Int32 sprite_i = 0;
        foreach (SpriteUsage s in list)
        {
            lines.Add($"    Sprite {sprite_i++}:");
            foreach (SectionTime t in s.Timeline)
                lines.Add($"        [{t}]");
        }
        return String.Join("\n", lines);
    }

    /// <summary>The layer every sprite from this allocator is created on.</summary>
    public StoryboardLayer GetLayer()
        => _layer;

    /// <summary>The mapset-relative texture path every sprite from this allocator uses.</summary>
    public String GetSpritePath()
        => _path;

    /// <summary>
    /// Whether the most recent <see cref="Allocate(SectionTime, OsbOrigin)"/> created a new
    /// sprite rather than reusing one. Check it to decide whether one-time setup commands
    /// (colour, additive, flips) still need emitting.
    /// </summary>
    public bool LastSpriteNew
        => _lastSpriteNew;

    /// <inheritdoc cref="Allocate(SectionTime, OsbOrigin)"/>
    public OsbSprite Allocate(Int32 st, Int32 et, OsbOrigin origin)
        => Allocate<Object?>(new(st, et), origin, null, null);

    /// <summary>
    /// Reserves a sprite for <paramref name="interval"/>, reusing a pooled one whose
    /// reservations do not overlap, or creating a new one when none is free.
    /// </summary>
    public OsbSprite Allocate(SectionTime interval, OsbOrigin origin)
        => Allocate<Object?>(interval, origin, null, null);

    /// <inheritdoc cref="Allocate{TArgs}(SectionTime, OsbOrigin, Action{OsbSprite, TArgs}, TArgs)"/>
    public OsbSprite Allocate<TArgs>(
        Int32 st,
        Int32 et,
        OsbOrigin origin,
        Action<OsbSprite, TArgs>? on_new_alloc,
        TArgs args)
        => Allocate(new(st, et), origin, on_new_alloc, args);

    /// <summary>
    /// Reserves a sprite for <paramref name="interval"/>. <paramref name="on_new_alloc"/>
    /// runs only when a sprite is actually created, which is where one-time setup belongs.
    /// </summary>
    public OsbSprite Allocate<TArgs>(
        SectionTime interval,
        OsbOrigin origin,
        Action<OsbSprite, TArgs>? on_new_alloc,
        TArgs args)
    {
        if (!_spritesByOrigin.TryGetValue(origin, out List<SpriteUsage>? list))
        {   // This Origin enum type hasn't been used yet:
            list = [];
            _spritesByOrigin[origin] = list;
        }
        foreach (SpriteUsage su in list)
        {
            if (CanReuse(su.Timeline, interval))
            {
                InsertSorted(su.Timeline, interval);
                _lastSpriteNew = false;
                return su.Sprite;
            }
        }
        // No reuseable sprites during given interval:
        SpriteUsage new_alloc = new(_layer.CreateSprite(_path, origin), interval);
        list.Add(new_alloc);
        on_new_alloc?.Invoke(new_alloc.Sprite, args);
        _lastSpriteNew = true;

        return new_alloc.Sprite;
    }

    private static bool CanReuse(List<SectionTime> list, SectionTime t)
    {
        Int32 idx = FindInsertIndex(list, t);

        // Check previous and possible next interval:
        if (idx > 0 && list[idx - 1].Et > t.St)
            return false;
        if (idx < list.Count && list[idx].St < t.Et)
            return false;

        return true;
    }

    private static void InsertSorted(List<SectionTime> list, SectionTime t)
    {
        Int32 idx = FindInsertIndex(list, t);
        list.Insert(idx, t);
    }

    private static Int32 FindInsertIndex(List<SectionTime> list, SectionTime t)
    {
        Int32 lo = 0, hi = list.Count;
        while (lo < hi)
        {
            Int32 mid = (lo + hi) / 2;
            if (list[mid].St < t.St)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }
}

/// <summary>
/// Buffers log lines in memory and flushes them to
/// <c>&lt;ProjectPath&gt;/logs/&lt;script&gt;.log</c> on dispose. Storybrew has its own
/// <c>Log()</c>, but this survives past the effect panel and can be tailed from an editor.
/// </summary>
public sealed class Logger : IDisposable
{
    private readonly ConcurrentQueue<String> _log = [];
    private Int32 _disposed;
    private readonly bool _append;
    private readonly String _logPath;

    /// <param name="o">The calling script, used for <c>ProjectPath</c>.</param>
    /// <param name="append">Append to an existing log instead of overwriting it.</param>
    /// <param name="file">Compiler-supplied; names the log after the calling source file.</param>
    public Logger(
        StoryboardObjectGenerator o,
        bool append = false,
        [CallerFilePath] String file = "")
    {
        ArgumentNullException.ThrowIfNull(o);
        if (String.IsNullOrEmpty(o.ProjectPath))
            throw new ArgumentException("ProjectPath cannot be null or empty.");
        _logPath = Path.Combine(
            o.ProjectPath,
            "logs",
            $"{Path.GetFileNameWithoutExtension(
                String.IsNullOrEmpty(file) ? "Utilities" : file
            )}.log"
        );
        _append = append;
    }

    /// <summary>
    /// Queues one line, joining the arguments with ", ". Null arguments render as
    /// <c>&lt;null&gt;</c>. Calls after disposal are dropped.
    /// </summary>
    public void Log(params Object?[]? objects)
    {
        if (Volatile.Read(ref _disposed) != 0 || objects?.Length == 0)
            return;
        if (objects == null)
        {
            _log.Enqueue("<null>");
            return;
        }
        String[] parts = new String[objects.Length];
        for (Int32 i = 0; i < objects.Length; ++i)
            parts[i] = objects[i]?.ToString() ?? "<null>";
        _log.Enqueue(String.Join(", ", parts));
    }

    private void WriteLogs()
    {
        if (_log.IsEmpty)
            return;
        String? dir = Path.GetDirectoryName(_logPath);
        if (!String.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        using StreamWriter writer = new(_logPath, _append);
        while (_log.TryDequeue(out String? log))
            writer.WriteLine(log);
    }

    /// <summary>Flushes the queue to disk. Safe to call from any thread, and idempotent.</summary>
    public void Dispose()
    {
        // CompareExchange rather than a check-then-set on a bool, so two threads racing
        // Dispose cannot both reach WriteLogs and write the file twice.
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return;
        WriteLogs();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Easing curves, interpolation and 2d geometry helpers.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EaseFunctions"/> deliberately reimplements curves that
/// <c>StorybrewCommon.Animations.EasingFunctions</c> already ships, because these are
/// needed as plain <c>Func&lt;Double, Double&gt;</c> values that
/// <see cref="SubEase(Func{Double, Double}, Double, Double, bool)"/> can
/// slice and resample. The two agree: InOutBack and OutBounce are bit-identical, and the
/// elastic pair differs by under 5e-4 (only because the endpoints are special-cased here,
/// which is the more correct behaviour). Do not "simplify" this away by switching to
/// Storybrew, it would change every baked curve at its endpoints.
/// </para>
/// <para>Not too SIMD-friendly, could be optimized but this isn't hpc code lul.</para>
/// </remarks>
public static class OsbMath
{
    private static readonly Dictionary<OsbEasing, Func<Double, Double>> _easeMap =
    new()
    {
        { OsbEasing.None, EaseFunctions.None },

        { OsbEasing.In,  EaseFunctions.IQuad },
        { OsbEasing.Out, EaseFunctions.OQuad },

        { OsbEasing.InSine,    EaseFunctions.ISine },
        { OsbEasing.OutSine,   EaseFunctions.OSine },
        { OsbEasing.InOutSine, EaseFunctions.IOSine },

        { OsbEasing.InQuad,    EaseFunctions.IQuad },
        { OsbEasing.OutQuad,   EaseFunctions.OQuad },
        { OsbEasing.InOutQuad, EaseFunctions.IOQuad },

        { OsbEasing.InCubic,    EaseFunctions.ICubic },
        { OsbEasing.OutCubic,   EaseFunctions.OCubic },
        { OsbEasing.InOutCubic, EaseFunctions.IOCubic },

        { OsbEasing.InQuart,    EaseFunctions.IQuart },
        { OsbEasing.OutQuart,   EaseFunctions.OQuart },
        { OsbEasing.InOutQuart, EaseFunctions.IOQuart },

        { OsbEasing.InQuint,    EaseFunctions.IQuint },
        { OsbEasing.OutQuint,   EaseFunctions.OQuint },
        { OsbEasing.InOutQuint, EaseFunctions.IOQuint },

        { OsbEasing.InExpo,    EaseFunctions.IExpo },
        { OsbEasing.OutExpo,   EaseFunctions.OExpo },
        { OsbEasing.InOutExpo, EaseFunctions.IOExpo },

        { OsbEasing.InCirc,    EaseFunctions.ICirc },
        { OsbEasing.OutCirc,   EaseFunctions.OCirc },
        { OsbEasing.InOutCirc, EaseFunctions.IOCirc },

        { OsbEasing.InBack,    EaseFunctions.IBack },
        { OsbEasing.OutBack,   EaseFunctions.OBack },
        { OsbEasing.InOutBack, EaseFunctions.IOBack },

        { OsbEasing.InElastic,    EaseFunctions.IElastic },
        { OsbEasing.OutElastic,   EaseFunctions.OElastic },
        { OsbEasing.InOutElastic, EaseFunctions.IOElastic },

        { OsbEasing.OutElasticHalf,    EaseFunctions.OElasticHalf },
        { OsbEasing.OutElasticQuarter, EaseFunctions.OElasticQuarter },

        { OsbEasing.InBounce,    EaseFunctions.IBounce },
        { OsbEasing.OutBounce,   EaseFunctions.OBounce },
        { OsbEasing.InOutBounce, EaseFunctions.IOBounce },
    };

    // t should be between 0d and 1d:
    public static class EaseFunctions
    {
        private const Double PI = Math.PI;
        private const Double C1 = 1.70158d;
        private const Double C4 = (2d * PI) / 3d;

        public static Double None(Double t)
            => t;
        public static Double ISine(Double t)
            => 1d - Math.Cos((t * PI) / 2d);
        public static Double OSine(Double t)
            => Math.Sin((t * PI) / 2d);
        public static Double IOSine(Double t)
            => -0.5d * (Math.Cos(t * PI) - 1d);
        public static Double IQuad(Double t)
            => t * t;
        public static Double OQuad(Double t)
            => 1d - (1d - t) * (1d - t);
        public static Double IOQuad(Double t)
            => t < 0.5d
                ? 2d * t * t
                : 1d - Math.Pow(-2d * t + 2d, 2d) / 2d;
        public static Double ICubic(Double t)
            => t * t * t;
        public static Double OCubic(Double t)
            => 1d - Math.Pow(1d - t, 3d);
        public static Double IOCubic(Double t)
            => t < 0.5d
                ? 4d * t * t * t
                : 1d - Math.Pow(-2d * t + 2d, 3d) / 2d;
        public static Double IQuart(Double t)
            => t * t * t * t;
        public static Double OQuart(Double t)
            => 1d - Math.Pow(1d - t, 4d);
        public static Double IOQuart(Double t)
            => t < 0.5d
                ? 8d * Math.Pow(t, 4d)
                : 1d - Math.Pow(-2d * t + 2d, 4d) / 2d;
        public static Double IQuint(Double t)
            => t * t * t * t * t;
        public static Double OQuint(Double t)
            => 1d - Math.Pow(1d - t, 5d);
        public static Double IOQuint(Double t)
            => t < 0.5d
                ? 16d * Math.Pow(t, 5d)
                : 1d - Math.Pow(-2d * t + 2d, 5d) / 2d;
        public static Double IExpo(Double t)
            => t == 0d ? 0d : Math.Pow(2d, 10d * t - 10d);
        public static Double OExpo(Double t)
            => t == 1d ? 1d : 1d - Math.Pow(2d, -10d * t);
        public static Double IOExpo(Double t)
        {
            if (t == 0d) return 0d;
            if (t == 1d) return 1d;
            return t < 0.5d
                ? Math.Pow(2d, 20d * t - 10d) / 2d
                : (2d - Math.Pow(2d, -20d * t + 10d)) / 2d;
        }
        public static Double ICirc(Double t)
            => 1d - Math.Sqrt(1d - t * t);
        public static Double OCirc(Double t)
            => Math.Sqrt(1d - Math.Pow(t - 1d, 2d));
        public static Double IOCirc(Double t)
            => t < 0.5d
                ? (1d - Math.Sqrt(1d - Math.Pow(2d * t, 2d))) / 2d
                : (Math.Sqrt(1d - Math.Pow(-2d * t + 2d, 2d)) + 1d) / 2d;
        // The cubic term uses C1 + 1 (easings.net calls it c3), not C1. With C1 on both
        // terms IBack(1) lands on 0 and OBack(0) lands on 1, so the curve ends where it
        // started.
        public static Double IBack(Double t)
            => (C1 + 1d) * t * t * t - C1 * t * t;
        public static Double OBack(Double t)
        {
            Double x = t - 1d;
            return 1d + (C1 + 1d) * x * x * x + C1 * x * x;
        }
        public static Double IOBack(Double t)
        {
            const Double C2 = C1 * 1.525d;
            return t < 0.5d
                ? Math.Pow(2d * t, 2d) * ((C2 + 1d) * 2d * t - C2) / 2d
                : (Math.Pow(2d * t - 2d, 2d)
                    * ((C2 + 1d) * (t * 2d - 2d) + C2)
                    + 2d) / 2d;
        }
        public static Double IElastic(Double t)
        {
            if (t == 0d) return 0d;
            if (t == 1d) return 1d;
            return -Math.Pow(2d, 10d * t - 10d) *
                   Math.Sin((t * 10d - 10.75d) * C4);
        }
        public static Double OElastic(Double t)
        {
            if (t == 0d) return 0d;
            if (t == 1d) return 1d;
            return Math.Pow(2d, -10d * t) *
                   Math.Sin((t * 10d - 0.75d) * C4) + 1d;
        }
        public static Double IOElastic(Double t)
        {
            const Double C5 = (2d * PI) / 4.5d;
            if (t == 0d) return 0d;
            if (t == 1d) return 1d;
            return t < 0.5d
                ? -(Math.Pow(2d, 20d * t - 10d)
                    * Math.Sin((20d * t - 11.125d) * C5)) / 2d
                : (Math.Pow(2d, -20d * t + 10d)
                    * Math.Sin((20d * t - 11.125d) * C5)) / 2d + 1d;
        }
        public static Double OElasticHalf(Double t)
        {
            const Double C4_HALF = (2d * PI) / 1.5d;
            if (t == 0d) return 0d;
            if (t == 1d) return 1d;
            return Math.Pow(2d, -10d * t) *
                   Math.Sin((t * 10d - 0.75d) * C4_HALF) + 1d;
        }
        public static Double OElasticQuarter(Double t)
        {
            const Double C4_QUARTER = (2d * PI) / 0.75d;
            if (t == 0d) return 0d;
            if (t == 1d) return 1d;
            return Math.Pow(2d, -10d * t) *
                   Math.Sin((t * 10d - 0.75d) * C4_QUARTER) + 1d;
        }
        private static Double BounceOut(Double t)
        {
            const Double N1 = 7.5625d;
            const Double D1 = 2.75d;
            if (t < 1d / D1)
                return N1 * t * t;
            else if (t < 2d / D1)
                return N1 * (t -= 1.5d / D1) * t + 0.75d;
            else if (t < 2.5d / D1)
                return N1 * (t -= 2.25d / D1) * t + 0.9375d;
            else
                return N1 * (t -= 2.625d / D1) * t + 0.984375d;
        }
        public static Double OBounce(Double t)
            => BounceOut(t);

        public static Double IBounce(Double t)
            => 1d - BounceOut(1d - t);

        public static Double IOBounce(Double t)
            => t < 0.5d
                ? (1d - BounceOut(1d - 2d * t)) / 2d
                : (1d + BounceOut(2d * t - 1d)) / 2d;
    }

    public static void FillEase(
        Span<Double> buffer,
        Double min,
        Double max,
        OsbEasing ease = Ease.None,
        bool clamp = false)
        => FillEase(buffer, min, max, GetEaseFunction(ease), clamp);

    public static void FillEase(
        Span<Double> buffer,
        Double min,
        Double max,
        Func<Double, Double> ease,
        bool clamp = false)
    {
        if (buffer.Length == 0)
            return;
        if (buffer.Length == 1)
        {
            buffer[0] = min;
            return;
        }
        Int32 n = buffer.Length;
        for (Int32 i = 0; i < n; ++i)
        {
            Double t = (Double)i / (n - 1);
            Double eased = ease(t);
            if (clamp)
                eased = Math.Clamp(eased, 0d, 1d);
            buffer[i] = Lerp(min, max, eased);
        }
    }

    /// <summary>
    /// Slices an easing curve to its [t0, t1] sub-interval. Input t in [0,1] maps to
    /// [t0, t1] of the source curve.
    /// </summary>
    /// <param name="ease">The curve to slice.</param>
    /// <param name="t0">Slice start, in [0, 1] and below <paramref name="t1"/>.</param>
    /// <param name="t1">Slice end, in [0, 1].</param>
    /// <param name="renormalize">
    /// When true (the default) the output is rescaled so the slice still spans [0, 1],
    /// which is what you want for "skip the slow tail" or "skip the slow start" effects.
    /// When false the raw f(t0)..f(t1) segment is returned, which is what you want when
    /// chaining commands across one continuous curve. A slice with zero rise
    /// (f(t0) == f(t1)) cannot be rescaled, so it falls back to the raw segment and
    /// returns a constant.
    /// </param>
    public static Func<Double, Double> SubEase(
        OsbEasing ease,
        Double t0,
        Double t1,
        bool renormalize = true)
        => SubEase(GetEaseFunction(ease), t0, t1, renormalize);

    public static Func<Double, Double> SubEase(
        Func<Double, Double> ease,
        Double t0,
        Double t1,
        bool renormalize = true)
    {
        if (t0 >= t1)
            throw new ArgumentException("t0 must be < t1", nameof(t0));
        if (t0 < 0d || t1 > 1d)
            throw new ArgumentException("t0 and t1 must be in [0, 1]");

        Double dt = t1 - t0;
        Double y0 = ease(t0);
        Double y1 = ease(t1);
        Double dy = y1 - y0;

        if (renormalize && dy != 0d)
            return t => (ease(t0 + dt * t) - y0) / dy;
        return t => ease(t0 + dt * t);
    }

    public static List<Double> CreateEaseList(
        Int32 n,
        Double min,
        Double max,
        OsbEasing ease = Ease.None)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(n, nameof(n));
        if (n == 1)
            return [min];

        List<Double> result = new(n);
        result.AddRange(new Double[n]); // Single allocation.
        FillEase(CollectionsMarshal.AsSpan(result), min, max, ease);

        return result;
    }

    public static Double EvaluateEaseAtTime(Double t, OsbEasing ease, bool clamp = false)
    {
        Func<Double, Double> easing_func = GetEaseFunction(ease);
        Double eased = easing_func(t);
        if (clamp)
            return Math.Clamp(eased, 0d, 1d);

        return eased;
    }

    public static Func<Double, Double> GetEaseFunction(OsbEasing ease)
    {
        if (!_easeMap.TryGetValue(ease, out Func<Double, Double>? easing_func))
        {
            throw new ArgumentException(
                $"Easing '{ease}' not implemented.",
                nameof(ease)
            );
        }
        return easing_func;
    }

    public static Double Lerp(Double a, Double b, Double v)
        => a + (b - a) * v;

    public static Double InverseLerp(Double a, Double b, Double v)
    {
        if (a == b)
            return 0d;
        return (v - a) / (b - a);
    }

    public static Double Remap(
        Double in_min, Double in_max,
        Double out_min, Double out_max,
        Double v)
    {
        Double t = InverseLerp(in_min, in_max, v);
        return Lerp(out_min, out_max, t);
    }

    /// <summary>Rounds to <paramref name="decimals"/> places. The cheapest osb size win there is.</summary>
    public static Double Quantize(Double v, Int32 decimals)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decimals, nameof(decimals));

        Double scale = Math.Pow(10d, decimals);
        return Math.Round(v * scale) / scale;
    }

    public static Double Deadzone(Double v, Double epsilon = 1e-6d)
        => Math.Abs(v) < epsilon ? 0d : v;

    public static Double Snap(Double v, Double step)
        => Math.Round(v / step) * step;

    public static IEnumerable<Double> DeduplicateConsecutiveValues(
        IEnumerable<Double> values,
        Double epsilon = 1e-6d)
    {
        Double? last = null;

        foreach (Double v in values)
        {
            if (last == null || Math.Abs(v - last.Value) > epsilon)
            {
                yield return v;
                last = v;
            }
        }
    }

    public static Double Distance(CommandPosition a, CommandPosition b)
    {
        Double dx = b.X - a.X;
        Double dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Angle of the vector a -> b, in radians.</summary>
    public static Double Angle(CommandPosition a, CommandPosition b)
        => Math.Atan2(b.Y - a.Y, b.X - a.X);

    public readonly struct LineData(
        CommandDecimal length,
        CommandDecimal rotation,
        CommandPosition mid)
    {
        public readonly CommandDecimal Length = length;
        public readonly CommandDecimal Rotation = rotation;
        public readonly CommandPosition Mid = mid;
    }

    public static LineData LineFromPoints(
        CommandPosition a,
        CommandPosition b,
        Double rotation_offset = 0d)
    {
        Double dx = b.X - a.X;
        Double dy = b.Y - a.Y;

        CommandDecimal length = Math.Sqrt(dx * dx + dy * dy);
        CommandDecimal rotation = Math.Atan2(dy, dx) - rotation_offset;
        CommandPosition mid = new(
            (a.X + b.X) / 2d,
            (a.Y + b.Y) / 2d
        );

        return new(length, rotation, mid);
    }

    public static CommandPosition ProjectPointOnLine(
        CommandPosition p,
        CommandPosition a,
        CommandPosition b)
    {
        Double dx = b.X - a.X;
        Double dy = b.Y - a.Y;

        Double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy)
                 / (dx * dx + dy * dy);

        return new(a.X + dx * t, a.Y + dy * t);
    }

    /// <summary>Point at fraction <paramref name="t"/> along the segment a -> b.</summary>
    public static CommandPosition PointOnLine(
        CommandPosition a,
        CommandPosition b,
        Double t)
        => new(Lerp(a.X, b.X, t), Lerp(a.Y, b.Y, t));

    /// <summary>Polar to cartesian, around <paramref name="origin"/>. Angle in radians.</summary>
    public static CommandPosition FromPolar(
        CommandPosition origin,
        Double angle,
        Double radius)
        => new(
            origin.X + Math.Cos(angle) * radius,
            origin.Y + Math.Sin(angle) * radius
        );

    public static CommandPosition RotateAroundPivot(
        CommandPosition point,
        CommandPosition pivot,
        Double angle)
    {
        Double cos = Math.Cos(angle);
        Double sin = Math.Sin(angle);

        Double dx = point.X - pivot.X;
        Double dy = point.Y - pivot.Y;

        return new(
            pivot.X + dx * cos - dy * sin,
            pivot.Y + dx * sin + dy * cos
        );
    }

    public static (Double x, Double y) NormalizeVector(Double x, Double y)
    {
        Double len = Math.Sqrt(x * x + y * y);
        if (len == 0d)
            return (0d, 0d);
        return (x / len, y / len);
    }

    /// <summary>Yields exactly <paramref name="count"/> evenly spaced values across [0, 1].</summary>
    public static IEnumerable<Double> Linspace(Int32 count)
    {
        if (count <= 1)
        {
            yield return 0d;
            yield break;
        }

        Double step = 1d / (count - 1);
        for (Int32 i = 0; i < count; ++i)
            yield return i * step;
    }

    /// <summary>
    /// Yields exactly <paramref name="count"/> values of <paramref name="ease"/> sampled
    /// across [0, 1]. Note the count convention differs from
    /// <see cref="OsbCommands.Sample"/> and <see cref="OsbCommands.EaseKeyframes(Int32, Int32, Single, Single, Func{Double, Double}, Int32)"/>,
    /// which take a number of intervals and emit one more keyframe than that.
    /// </summary>
    public static IEnumerable<Double> EaseSamples(Int32 count, OsbEasing ease)
    {
        Func<Double, Double> func = GetEaseFunction(ease);
        foreach (Double t in Linspace(count))
            yield return func(t);
    }

    public static Double NormalizeTime(Double time, SectionTime t)
        => NormalizeTime(time, t.St, t.Et);

    public static Double NormalizeTime(Double time, Double start, Double end)
        => (time - start) / (end - start);
}

public interface ISectionTime
{
    public Int32 St { get; }
    public Int32 Et { get; }
    public Int32 Lt { get; }
}

/// <summary>
/// A section that owns a sprite sub-directory. Implementors declare their own
/// <c>operator /</c>; one on this interface would be shadowed by theirs and never bind.
/// </summary>
public interface ISectionPath
{
    public String Path { get; }
}

public readonly struct SectionPath(String section_path) : ISectionPath
{
    public String Path { get; } = section_path;
    public static String operator /(SectionPath s, String path)
        => s.Path + "/" + path;
    public static String operator /(SectionPath lhs, SectionPath rhs)
        => lhs.Path + "/" + rhs.Path;
}

/// <summary>A half-open [St, Et) span in song milliseconds.</summary>
public readonly struct SectionTime : ISectionTime
{
    public Int32 St { get; }
    public Int32 Et { get; }

    /// <summary>Length in milliseconds.</summary>
    public Int32 Lt => Et - St;

    public SectionTime(Int32 st, Int32 et)
    {
        if (st >= et)
            throw new ArgumentException("[SectionTime]: Invalid interval");
        St = st;
        Et = et;
    }

    public override String ToString()
        => St.ToString() + "," + Et.ToString();
}

/// <summary>
/// A section: its sprite sub-directory plus its [St, Et) span. <c>s / "sub"</c> builds a
/// path below it.
/// </summary>
public readonly struct SectionInfo : ISectionPath, ISectionTime
{
    public Int32 St { get; }
    public Int32 Et { get; }

    /// <summary>Length in milliseconds.</summary>
    public Int32 Lt => Et - St;
    public String Path { get; }

    public SectionInfo(String section_path, Int32 st, Int32 et)
    {
        // Matches SectionTime: a zero-length or reversed section is always a mistake.
        if (st >= et)
            throw new ArgumentException("[SectionInfo]: Invalid interval");
        Path = section_path;
        St = st;
        Et = et;
    }

    public static String operator /(SectionInfo s, String path)
        => s.Path + "/" + path;
    public static String operator /(SectionInfo lhs, SectionInfo rhs)
        => lhs.Path + "/" + rhs.Path;
}

/// <summary>
/// Base for a script-side sprite record: the osb sprite plus the spawn state a generator
/// needs to remember about it.
/// </summary>
public abstract class SpriteBase(
    OsbSprite? spr,
    Int32 st,
    Int32 et,
    CommandPosition pos,
    Double scale) : ISectionTime
{
    public Int32 St { get; } = st;
    public Int32 Et { get; } = et;
    public Int32 Lt => Et - St;
    public OsbSprite? Sprite { get; set; } = spr;
    public CommandPosition Position0 { get; set; } = pos;
    public Double Scale0 { get; set; } = scale;
}

/// <summary>Shorthand aliases for <see cref="OsbEasing"/>. I = In, O = Out, IO = InOut.</summary>
public static class Ease
{
    public const OsbEasing None = OsbEasing.None;

    public const OsbEasing I = OsbEasing.In;
    public const OsbEasing O = OsbEasing.Out;

    public const OsbEasing IQuad = OsbEasing.InQuad;
    public const OsbEasing OQuad = OsbEasing.OutQuad;
    public const OsbEasing IOQuad = OsbEasing.InOutQuad;

    public const OsbEasing ICubic = OsbEasing.InCubic;
    public const OsbEasing OCubic = OsbEasing.OutCubic;
    public const OsbEasing IOCubic = OsbEasing.InOutCubic;

    public const OsbEasing IQuart = OsbEasing.InQuart;
    public const OsbEasing OQuart = OsbEasing.OutQuart;
    public const OsbEasing IOQuart = OsbEasing.InOutQuart;

    public const OsbEasing IQuint = OsbEasing.InQuint;
    public const OsbEasing OQuint = OsbEasing.OutQuint;
    public const OsbEasing IOQuint = OsbEasing.InOutQuint;

    public const OsbEasing ISine = OsbEasing.InSine;
    public const OsbEasing OSine = OsbEasing.OutSine;
    public const OsbEasing IOSine = OsbEasing.InOutSine;

    public const OsbEasing IExpo = OsbEasing.InExpo;
    public const OsbEasing OExpo = OsbEasing.OutExpo;
    public const OsbEasing IOExpo = OsbEasing.InOutExpo;

    public const OsbEasing ICirc = OsbEasing.InCirc;
    public const OsbEasing OCirc = OsbEasing.OutCirc;
    public const OsbEasing IOCirc = OsbEasing.InOutCirc;

    public const OsbEasing IElastic = OsbEasing.InElastic;
    public const OsbEasing OElastic = OsbEasing.OutElastic;
    public const OsbEasing IOElastic = OsbEasing.InOutElastic;

    public const OsbEasing OElasticHalf = OsbEasing.OutElasticHalf;
    public const OsbEasing OElasticQuarter = OsbEasing.OutElasticQuarter;

    public const OsbEasing IBack = OsbEasing.InBack;
    public const OsbEasing OBack = OsbEasing.OutBack;
    public const OsbEasing IOBack = OsbEasing.InOutBack;

    public const OsbEasing IBounce = OsbEasing.InBounce;
    public const OsbEasing OBounce = OsbEasing.OutBounce;
    public const OsbEasing IOBounce = OsbEasing.InOutBounce;
}

/// <summary>Shorthand aliases for <see cref="OsbOrigin"/>. T/C/B = top/centre/bottom, L/C/R = left/centre/right.</summary>
public static class Origin
{
    public const OsbOrigin TL = OsbOrigin.TopLeft;
    public const OsbOrigin TC = OsbOrigin.TopCentre;
    public const OsbOrigin TR = OsbOrigin.TopRight;

    public const OsbOrigin CL = OsbOrigin.CentreLeft;
    public const OsbOrigin C = OsbOrigin.Centre;
    public const OsbOrigin CR = OsbOrigin.CentreRight;

    public const OsbOrigin BL = OsbOrigin.BottomLeft;
    public const OsbOrigin BC = OsbOrigin.BottomCentre;
    public const OsbOrigin BR = OsbOrigin.BottomRight;
}

/// <summary>Shorthand aliases for <see cref="OsbLoopType"/>. LO = LoopOnce, LF = LoopForever.</summary>
public static class Loop
{
    public const OsbLoopType LO = OsbLoopType.LoopOnce;
    public const OsbLoopType LF = OsbLoopType.LoopForever;
}

/// <summary>
/// The keyframe pipeline: sample a curve, thin it with <see cref="Optimize(KeyframedValue{Single}, Double)"/>,
/// emit it with one of the <c>Apply*</c> extensions.
/// </summary>
public static class OsbCommands
{
    /// <summary>
    /// Creates an animation and returns it together with the span of one full cycle (the W is
    /// for "wrapped"), so the caller does not have to recompute frames * delay to time a
    /// fade-out on a cycle boundary.
    /// </summary>
    /// <param name="lyr">Layer to create the animation on.</param>
    /// <param name="sprite_path">Mapset-relative path, un-numbered (see <c>CreateAnimation</c>).</param>
    /// <param name="frame_n">Frame count.</param>
    /// <param name="frame_delay">Milliseconds per frame.</param>
    /// <param name="st">Start time, song milliseconds. The returned span runs from here.</param>
    /// <param name="loop">Loop behaviour. Defaults to <see cref="Loop.LO"/>.</param>
    /// <param name="origin">Sprite origin. Defaults to <see cref="Origin.C"/>.</param>
    /// <returns>The animation, and [st, st + frame_delay * frame_n).</returns>
    public static (OsbAnimation Animation, SectionTime Time) CreateAnimationW(
        StoryboardLayer lyr,
        String sprite_path,
        Int32 frame_n,
        Int32 frame_delay,
        Int32 st,
        OsbLoopType loop = Loop.LO,
        OsbOrigin origin = Origin.C)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frame_n, nameof(frame_n));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frame_delay, nameof(frame_delay));

        OsbAnimation animation = lyr.CreateAnimation(
            sprite_path, frame_n, frame_delay, loop, origin
        );
        return (animation, new(st, st + frame_delay * frame_n));
    }

    /// <summary>
    /// Samples <paramref name="sampler"/> across [start, end]. Emits
    /// <paramref name="samples"/> + 1 keyframes, since both endpoints are included.
    /// </summary>
    public static KeyframedValue<T> Sample<T>(
        Int32 samples,
        Double start,
        Double end,
        Func<Double, T> sampler,
        Func<T, T, Double, T> lerp_func)
    {
        KeyframedValue<T> kf = new(lerp_func);
        for (Int32 i = 0; i <= samples; ++i)
        {
            Double t = OsbMath.Lerp(start, end, i / (Double)samples);
            kf.Add(t, sampler(t));
        }
        return kf;
    }

    /// <summary>
    /// Bakes an easing curve (typically an <see cref="OsbMath.SubEase(OsbEasing, Double, Double, bool)"/>
    /// result) into keyframes spanning [st, et] from <paramref name="from"/> to
    /// <paramref name="to"/>. Feed the result through <c>Optimize</c> then one of the
    /// <c>Apply*</c> extensions.
    /// </summary>
    /// <param name="st">Start time, song milliseconds.</param>
    /// <param name="et">End time, song milliseconds. Must be after <paramref name="st"/>.</param>
    /// <param name="from">Value at <paramref name="st"/>.</param>
    /// <param name="to">Value at <paramref name="et"/>.</param>
    /// <param name="ease">The curve to bake.</param>
    /// <param name="samples">
    /// Number of intervals, trading smoothness against command count. Emits one more
    /// keyframe than this, since both endpoints are included.
    /// </param>
    public static KeyframedValue<Single> EaseKeyframes(
        Int32 st, Int32 et,
        Single from, Single to,
        Func<Double, Double> ease,
        Int32 samples = 32)
    {
        if (et <= st)
            throw new ArgumentException("et must be > st");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(samples, nameof(samples));

        KeyframedValue<Single> kf = new((a, b, t) => (Single)OsbMath.Lerp(a, b, t));
        Int32 dur = et - st;
        for (Int32 i = 0; i <= samples; ++i)
        {
            Double t = (Double)i / samples;
            Single v = (Single)OsbMath.Lerp(from, to, ease(t));
            kf.Add(st + (Int32)Math.Round(dur * t), v);
        }
        return kf;
    }

    /// <inheritdoc cref="EaseKeyframes(Int32, Int32, Single, Single, Func{Double, Double}, Int32)"/>
    public static KeyframedValue<Vector2> EaseKeyframes(
        Int32 st, Int32 et,
        Vector2 from, Vector2 to,
        Func<Double, Double> ease,
        Int32 samples = 32)
    {
        if (et <= st)
            throw new ArgumentException("et must be > st");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(samples, nameof(samples));

        KeyframedValue<Vector2> kf = new((a, b, t) => new Vector2(
            (Single)OsbMath.Lerp(a.X, b.X, t),
            (Single)OsbMath.Lerp(a.Y, b.Y, t)
        ));
        Int32 dur = et - st;
        for (Int32 i = 0; i <= samples; ++i)
        {
            Double t = (Double)i / samples;
            Double eased = ease(t);
            Vector2 v = new(
                (Single)OsbMath.Lerp(from.X, to.X, eased),
                (Single)OsbMath.Lerp(from.Y, to.Y, eased)
            );
            kf.Add(st + (Int32)Math.Round(dur * t), v);
        }
        return kf;
    }

    public static KeyframedValue<Single> EaseKeyframes(
        Int32 st, Int32 et,
        Single from, Single to,
        OsbEasing ease,
        Int32 samples = 32)
        => EaseKeyframes(st, et, from, to, OsbMath.GetEaseFunction(ease), samples);

    public static KeyframedValue<Vector2> EaseKeyframes(
        Int32 st, Int32 et,
        Vector2 from, Vector2 to,
        OsbEasing ease,
        Int32 samples = 32)
        => EaseKeyframes(st, et, from, to, OsbMath.GetEaseFunction(ease), samples);

    /// <summary>
    /// Rewrites an angle stream so consecutive keyframes never jump more than PI, stopping
    /// a sprite from unwinding the long way round a wrap. Call it before <c>Optimize</c>
    /// and before <see cref="ApplyRotate"/>; it relies on the keyframes being in time order.
    /// </summary>
    public static void UnwrapAngles(KeyframedValue<Single> kf)
    {
        if (kf.Count == 0)
            return;

        Single prev = 0;
        bool first = true;
        kf.Transform(k =>
        {
            Single r = k.Value;

            if (first)
            {
                prev = r;
                first = false;
            }
            else
            {
                while (r - prev > Math.PI)
                    r -= (Single)(Math.PI * 2d);
                while (r - prev < -Math.PI)
                    r += (Single)(Math.PI * 2d);
                prev = r;
            }
            return new Keyframe<Single>(k.Time, r, k.Ease);
        });
    }

    /// <summary>Rebuilds the stream with <paramref name="transform"/> applied to every keyframe.</summary>
    public static void Transform<T>(
        this KeyframedValue<T> kf,
        Func<Keyframe<T>, Keyframe<T>> transform)
    {
        List<Keyframe<T>> list = new(kf.Count);
        foreach (Keyframe<T> k in kf)
            list.Add(transform(k));
        kf.Clear();
        kf.AddRange(list);
    }

    /// <summary>
    /// Emits the stream as linear <c>M</c> commands, rounded to <paramref name="decimals"/>.
    /// Mutually exclusive with <see cref="ApplyMoveX"/> / <see cref="ApplyMoveY"/> on one sprite.
    /// </summary>
    public static void ApplyMove(
        this KeyframedValue<Vector2> kf,
        OsbSprite sprite,
        Int32 decimals = 2)
    {
        kf.ForEachPair((a, b) =>
            sprite.Move(
                Ease.None,
                a.Time, b.Time,
                OsbMath.Quantize(a.Value.X, decimals),
                OsbMath.Quantize(a.Value.Y, decimals),
                OsbMath.Quantize(b.Value.X, decimals),
                OsbMath.Quantize(b.Value.Y, decimals)
            )
        );
    }

    /// <summary>
    /// Emits the stream as linear <c>MX</c> commands, rounded to <paramref name="decimals"/>.
    /// </summary>
    /// <remarks>
    /// Mutually exclusive with <see cref="ApplyMove"/> on the same sprite: M and MoveX/MoveY
    /// are independent osb properties, and emitting both leaves both tracks live.
    /// </remarks>
    public static void ApplyMoveX(
        this KeyframedValue<Single> kf,
        OsbSprite sprite,
        Int32 decimals = 2)
    {
        kf.ForEachPair((a, b) =>
            sprite.MoveX(
                Ease.None,
                a.Time, b.Time,
                OsbMath.Quantize(a.Value, decimals),
                OsbMath.Quantize(b.Value, decimals)
            )
        );
    }

    /// <inheritdoc cref="ApplyMoveX"/>
    public static void ApplyMoveY(
        this KeyframedValue<Single> kf,
        OsbSprite sprite,
        Int32 decimals = 2)
    {
        kf.ForEachPair((a, b) =>
            sprite.MoveY(
                Ease.None,
                a.Time, b.Time,
                OsbMath.Quantize(a.Value, decimals),
                OsbMath.Quantize(b.Value, decimals)
            )
        );
    }

    /// <summary>
    /// Emits the stream as <c>V</c> commands driving height only, holding width at
    /// <paramref name="x_scale"/>. Mutually exclusive with <see cref="ApplyScale"/>.
    /// </summary>
    public static void ApplyScaleY(
        this KeyframedValue<Single> kf,
        OsbSprite sprite,
        Double x_scale = 1d,
        Int32 decimals = 2)
    {
        kf.ForEachPair((a, b) =>
            sprite.ScaleVec(
                Ease.None,
                a.Time, b.Time,
                new(x_scale, OsbMath.Quantize(a.Value, decimals)),
                new(x_scale, OsbMath.Quantize(b.Value, decimals))
            )
        );
    }

    /// <summary>
    /// Emits the stream as uniform <c>S</c> commands. Mutually exclusive with
    /// <see cref="ApplyScaleVec"/> / <see cref="ApplyScaleY"/> on one sprite.
    /// </summary>
    public static void ApplyScale(
        this KeyframedValue<Single> kf,
        OsbSprite sprite,
        Int32 decimals = 2)
    {
        kf.ForEachPair((a, b) =>
            sprite.Scale(
                Ease.None,
                a.Time, b.Time,
                new(OsbMath.Quantize(a.Value, decimals)),
                new(OsbMath.Quantize(b.Value, decimals))
            )
        );
    }

    /// <summary>
    /// Emits the stream as per-axis <c>V</c> commands. Mutually exclusive with
    /// <see cref="ApplyScale"/> on one sprite.
    /// </summary>
    public static void ApplyScaleVec(
        this KeyframedValue<Vector2> kf,
        OsbSprite sprite,
        Int32 decimals = 2)
    {
        kf.ForEachPair((a, b) =>
            sprite.ScaleVec(
                Ease.None,
                a.Time, b.Time,
                new(OsbMath.Quantize(a.Value.X, decimals), OsbMath.Quantize(a.Value.Y, decimals)),
                new(OsbMath.Quantize(b.Value.X, decimals), OsbMath.Quantize(b.Value.Y, decimals))
            )
        );
    }

    /// <summary>
    /// Emits the stream as <c>R</c> commands, in radians. Run <see cref="UnwrapAngles"/>
    /// first if the values can cross a wrap.
    /// </summary>
    public static void ApplyRotate(
        this KeyframedValue<Single> kf,
        OsbSprite sprite,
        Int32 decimals = 2)
    {
        kf.ForEachPair((a, b) =>
            sprite.Rotate(
                Ease.None,
                a.Time, b.Time,
                OsbMath.Quantize(a.Value, decimals),
                OsbMath.Quantize(b.Value, decimals)
            )
        );
    }

    /// <summary>
    /// Drops keyframes the surrounding ones already predict within
    /// <paramref name="tolerance"/>. Run this between the sampling step and the
    /// <c>Apply*</c> step; it is where most of the osb size saving comes from.
    /// </summary>
    public static void Optimize(this KeyframedValue<Single> kf, Double tolerance)
        => kf.Simplify1dKeyframes(tolerance, v => v);

    /// <inheritdoc cref="Optimize(KeyframedValue{Single}, Double)"/>
    public static void Optimize(this KeyframedValue<Vector2> kf, Double tolerance)
        => kf.Simplify2dKeyframes(tolerance, v => v);
}

} // namespace StoryboardUtilities
