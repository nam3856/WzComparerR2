using System;
using System.Threading;
using UnityEngine;

namespace WzComparerR2.Unity
{
    /// <summary>Independent eye clock. Sprite substitution leaves body and effect animation time intact.</summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(WzSpriteAnimator))]
    public sealed class WzRandomBlink : MonoBehaviour
    {
        public WzBlinkProfile profile;
        [Min(.001f)] public float minInterval = 5;
        [Min(.001f)] public float maxInterval = 15;
        [Range(1, 8)] public int minBlinkCount = 1;
        [Range(1, 8)] public int maxBlinkCount = 2;
        [Min(.001f)] public float halfClosedDuration = 1f / 30f;
        [Min(.001f)] public float closedDuration = .05f;
        [Min(.001f)] public float doubleBlinkGap = .12f;

        public WzBlinkExpression CurrentExpression { get; private set; } = WzBlinkExpression.Open;
        public bool IsBlinking => phase != Phase.Waiting;
        public float SecondsUntilNextBurst => phase == Phase.Waiting ? (float)timeRemaining : 0;
        public int LastBurstBlinkCount { get; private set; }
        public int StartedBurstCount { get; private set; }
        public int CompletedBurstCount { get; private set; }
        public int CompletedBlinkCount { get; private set; }
        public event Action<int> BurstStarted;
        public event Action<int> BurstCompleted;

        private enum Phase { Waiting, Closing, Closed, Opening, BetweenBlinks }
        private const int MaximumTransitionsPerAdvance = 256;
        private static int nextSeed = Environment.TickCount;
        private System.Random random;
        private Phase phase;
        private double timeRemaining;
        private int remainingBlinks;
        private WzSpriteAnimator animator;
        private Func<Sprite, Sprite> previousResolver;
        private Func<Sprite, Sprite> installedResolver;

        private void OnEnable()
        {
            animator = GetComponent<WzSpriteAnimator>();
            installedResolver = ResolveSprite;
            previousResolver = animator.SpriteResolver;
            animator.SpriteResolver = installedResolver;
            ResetSchedule(Interlocked.Increment(ref nextSeed));
        }

        private void OnDisable()
        {
            phase = Phase.Waiting;
            timeRemaining = 0;
            remainingBlinks = 0;
            CurrentExpression = WzBlinkExpression.Open;
            random = null;
            if (animator != null && animator.SpriteResolver == installedResolver)
            {
                animator.SpriteResolver = previousResolver;
                animator.RefreshPose();
            }
            previousResolver = null;
            installedResolver = null;
        }

        private void Update() => Advance(Time.deltaTime);
        private void OnValidate() => ClampSettings();

        /// <summary>Starts an open-eye waiting period with a reproducible independent random sequence.</summary>
        public void ResetSchedule(int seed)
        {
            ClampSettings();
            random = new System.Random(seed);
            LastBurstBlinkCount = 0;
            StartedBurstCount = 0;
            CompletedBurstCount = 0;
            CompletedBlinkCount = 0;
            remainingBlinks = 0;
            BeginWait();
            CurrentExpression = WzBlinkExpression.Open;
            if (animator != null) animator.RefreshPose();
        }

        /// <summary>Advances only eyes; accepts zero for paused gameplay and ignores invalid time values.</summary>
        public void Advance(float seconds)
        {
            if (seconds <= 0 || float.IsNaN(seconds) || float.IsInfinity(seconds)) return;
            if (random == null) ResetSchedule(Interlocked.Increment(ref nextSeed));
            ClampSettings();
            double remaining = seconds;
            for (int transitions = 0; transitions < MaximumTransitionsPerAdvance && remaining > 0; transitions++)
            {
                if (remaining < timeRemaining)
                {
                    timeRemaining -= remaining;
                    return;
                }
                remaining -= timeRemaining;
                Step();
            }
            // An extreme hitch drops excess catch-up time instead of emitting an unbounded number of blinks.
        }

        /// <summary>Manual preview or gameplay blink, followed by a fresh normal random waiting period.</summary>
        public void BlinkNow(int count = 1)
        {
            ClampSettings();
            if (random == null) random = new System.Random(Interlocked.Increment(ref nextSeed));
            BeginBurst(Mathf.Clamp(count, 1, 8));
        }

        private void Step()
        {
            switch (phase)
            {
                case Phase.Waiting:
                    BeginBurst(random.Next(minBlinkCount, maxBlinkCount + 1));
                    break;
                case Phase.Closing:
                    phase = Phase.Closed;
                    timeRemaining = closedDuration;
                    SetExpression(WzBlinkExpression.Closed);
                    break;
                case Phase.Closed:
                    phase = Phase.Opening;
                    timeRemaining = halfClosedDuration;
                    SetExpression(WzBlinkExpression.HalfClosed);
                    break;
                case Phase.Opening:
                    CompletedBlinkCount++;
                    remainingBlinks--;
                    if (remainingBlinks > 0)
                    {
                        phase = Phase.BetweenBlinks;
                        timeRemaining = doubleBlinkGap;
                        SetExpression(WzBlinkExpression.Open);
                    }
                    else
                    {
                        CompletedBurstCount++;
                        BeginWait();
                        SetExpression(WzBlinkExpression.Open);
                        BurstCompleted?.Invoke(LastBurstBlinkCount);
                    }
                    break;
                case Phase.BetweenBlinks:
                    phase = Phase.Closing;
                    timeRemaining = halfClosedDuration;
                    SetExpression(WzBlinkExpression.HalfClosed);
                    break;
            }
        }

        private void BeginBurst(int count)
        {
            phase = Phase.Closing;
            timeRemaining = halfClosedDuration;
            remainingBlinks = count;
            LastBurstBlinkCount = count;
            StartedBurstCount++;
            SetExpression(WzBlinkExpression.HalfClosed);
            BurstStarted?.Invoke(count);
        }

        private void BeginWait()
        {
            phase = Phase.Waiting;
            timeRemaining = minInterval + random.NextDouble() * (maxInterval - minInterval);
        }

        private void SetExpression(WzBlinkExpression expression)
        {
            if (CurrentExpression == expression) return;
            CurrentExpression = expression;
            if (animator != null) animator.RefreshPose();
        }

        private Sprite ResolveSprite(Sprite source)
        {
            if (previousResolver != null) source = previousResolver(source);
            return profile == null ? source : profile.Resolve(source, CurrentExpression);
        }

        private void ClampSettings()
        {
            minInterval = SafeDuration(minInterval, 5, 3600);
            maxInterval = Mathf.Max(minInterval, SafeDuration(maxInterval, 15, 3600));
            minBlinkCount = Mathf.Clamp(minBlinkCount, 1, 8);
            maxBlinkCount = Mathf.Clamp(maxBlinkCount, minBlinkCount, 8);
            halfClosedDuration = SafeDuration(halfClosedDuration, 1f / 30f, 5);
            closedDuration = SafeDuration(closedDuration, .05f, 5);
            doubleBlinkGap = SafeDuration(doubleBlinkGap, .12f, 5);
        }

        private static float SafeDuration(float value, float fallback, float maximum) =>
            float.IsNaN(value) || float.IsInfinity(value) ? fallback : Mathf.Clamp(value, .001f, maximum);
    }
}
