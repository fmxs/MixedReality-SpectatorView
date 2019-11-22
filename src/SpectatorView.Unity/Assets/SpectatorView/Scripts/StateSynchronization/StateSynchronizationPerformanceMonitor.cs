﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Profiling;

namespace Microsoft.MixedReality.SpectatorView
{
    public class StateSynchronizationPerformanceMonitor : Singleton<StateSynchronizationPerformanceMonitor>
    {
        public class MemoryUsage
        {
            public long TotalAllocatedMemoryDelta;
            public long TotalReservedMemoryDelta;
            public long TotalUnusedReservedMemoryDelta;

            public MemoryUsage()
            {
                TotalAllocatedMemoryDelta = 0;
                TotalReservedMemoryDelta = 0;
                TotalUnusedReservedMemoryDelta = 0;
            }

            public override string ToString()
            {
                return $"TotalAllocatedMemoryDelta:{TotalAllocatedMemoryDelta}, TotalReservedMemoryDelta:{TotalReservedMemoryDelta}, TotalUnusedReservedMemoryDelta:{TotalUnusedReservedMemoryDelta}";
            }
        }

        private struct PerformanceEventKey
        {
            public string ComponentName;
            public string EventName;

            public PerformanceEventKey(string componentName, string eventName)
            {
                this.ComponentName = componentName;
                this.EventName = eventName;
            }
        };

        public struct ParsedMessage
        {
            public bool PerformanceMonitoringEnabled;
            public List<Tuple<string, double>> EventDurations;
            public List<Tuple<string, double>> SummedEventDurations;
            public List<Tuple<string, int>> EventCounts;
            public List<Tuple<string, MemoryUsage>> MemoryUsages;

            public ParsedMessage(bool performanceMonitoringEnabled, List<Tuple<string, double>> eventDurations, List<Tuple<string, double>> summedDurations, List<Tuple<string, int>> eventCounts, List<Tuple<string, MemoryUsage>> memoryUsages)
            {
                this.PerformanceMonitoringEnabled = performanceMonitoringEnabled;
                this.EventDurations = eventDurations;
                this.SummedEventDurations = summedDurations;
                this.EventCounts = eventCounts;
                this.MemoryUsages = memoryUsages; 
            }

            public static ParsedMessage Empty => new ParsedMessage(false, null, null, null, null);
        };

        private Dictionary<PerformanceEventKey, Stopwatch> eventStopWatches = new Dictionary<PerformanceEventKey, Stopwatch>();
        private Dictionary<PerformanceEventKey, Stopwatch> incrementEventStopWatches = new Dictionary<PerformanceEventKey, Stopwatch>();
        private Dictionary<PerformanceEventKey, int> eventCounts = new Dictionary<PerformanceEventKey, int>();
        private Dictionary<PerformanceEventKey, MemoryUsage> eventMemoryUsage = new Dictionary<PerformanceEventKey, MemoryUsage>();

        protected override void Awake()
        {
            base.Awake();
            Profiler.enabled = StateSynchronizationPerformanceParameters.EnablePerformanceReporting;
        }

        public IDisposable IncrementEventDuration(string componentName, string eventName)
        {
            if (!StateSynchronizationPerformanceParameters.EnablePerformanceReporting)
            {
                return null;
            }

            var key = new PerformanceEventKey(componentName, eventName);
            if (!incrementEventStopWatches.TryGetValue(key, out var stopwatch))
            {
                stopwatch = new Stopwatch();
                incrementEventStopWatches.Add(key, stopwatch);
            }

            return new TimeScope(stopwatch);
        }

        public IDisposable MeasureEventDuration(string componentName, string eventName)
        {
            if (!StateSynchronizationPerformanceParameters.EnablePerformanceReporting)
            {
                return null;
            }

            var key = new PerformanceEventKey(componentName, eventName);
            if (!eventStopWatches.TryGetValue(key, out var stopwatch))
            {
                stopwatch = new Stopwatch();
                eventStopWatches.Add(key, stopwatch);
            }

            return new TimeScope(stopwatch);
        }

        public void IncrementEventCount(string componentName, string eventName)
        {
            if (!StateSynchronizationPerformanceParameters.EnablePerformanceReporting)
            {
                return;
            }

            var key = new PerformanceEventKey(componentName, eventName);
            if (!eventCounts.ContainsKey(key))
            {
                eventCounts.Add(key, 1);
            }
            else
            {
                eventCounts[key]++;
            }
        }
        
        public IDisposable MeasureEventMemoryUsage(string componentName, string eventName)
        {
            if (!StateSynchronizationPerformanceParameters.EnablePerformanceReporting)
            {
                UnityEngine.Debug.LogWarning($"Performance reporting was not enabled for memory usage event: {componentName}.{eventName}");
                return null;
            }

            var key = new PerformanceEventKey(componentName, eventName);
            if (!eventMemoryUsage.TryGetValue(key, out var memoryUsage))
            {
                memoryUsage = new MemoryUsage();
                eventMemoryUsage.Add(key, memoryUsage);
            }

            return new MemoryDelta(memoryUsage);
        }

        public void WriteMessage(BinaryWriter message, int numFrames)
        {
            if (StateSynchronizationPerformanceParameters.EnablePerformanceReporting)
            {
                message.Write(true);
            }
            else
            {
                message.Write(false);
                return;
            }

            double numFramesScale = (numFrames == 0) ? 1.0 : 1.0 / numFrames;
            List<Tuple<string, double>> durations = new List<Tuple<string, double>>();
            foreach(var pair in eventStopWatches)
            {
                durations.Add(new Tuple<string, double>($"{pair.Key.ComponentName}.{pair.Key.EventName}", pair.Value.Elapsed.TotalMilliseconds * numFramesScale));
                pair.Value.Reset();
            }

            message.Write(durations.Count);
            foreach(var duration in durations)
            {
                message.Write(duration.Item1);
                message.Write(duration.Item2);
            }

            message.Write(incrementEventStopWatches.Count);
            foreach (var pair in incrementEventStopWatches)
            {
                message.Write($"{pair.Key.ComponentName}.{pair.Key.EventName}");
                message.Write(pair.Value.Elapsed.TotalMilliseconds);
            }

            List<Tuple<string, int>> counts = new List<Tuple<string, int>>();
            foreach (var pair in eventCounts.ToList())
            {
                counts.Add(new Tuple<string, int>($"{pair.Key.ComponentName}.{pair.Key.EventName}", (int)(pair.Value * numFramesScale)));
                eventCounts[pair.Key] = 0;
            }

            message.Write(counts.Count);
            foreach(var count in counts)
            {
                message.Write(count.Item1);
                message.Write(count.Item2);
            }

            message.Write(eventMemoryUsage.Count);
            foreach (var pair in eventMemoryUsage)
            {
                message.Write($"{pair.Key.ComponentName}.{pair.Key.EventName}");
                message.Write(pair.Value.TotalAllocatedMemoryDelta);
                message.Write(pair.Value.TotalReservedMemoryDelta);
                message.Write(pair.Value.TotalUnusedReservedMemoryDelta);
            }
        }

        public static void ReadMessage(BinaryReader reader, out ParsedMessage message)
        {
            bool performanceMonitoringEnabled = reader.ReadBoolean();
            List<Tuple<string, double>> durations = null;
            List<Tuple<string, double>> summedDurations = null;
            List<Tuple<string, int>> counts = null;
            List<Tuple<string, MemoryUsage>> memoryUsages = null;

            if (!performanceMonitoringEnabled)
            {
                message = ParsedMessage.Empty;
                return;
            }

            int durationsCount = reader.ReadInt32();
            if (durationsCount > 0)
            {
                durations = new List<Tuple<string, double>>();
                for (int i = 0; i < durationsCount; i++)
                {
                    string eventName = reader.ReadString();
                    double eventDuration = reader.ReadDouble();
                    durations.Add(new Tuple<string, double>(eventName, eventDuration));
                }
            }

            int summedDurationsCount = reader.ReadInt32();
            if (summedDurationsCount > 0)
            {
                summedDurations = new List<Tuple<string, double>>();
                for (int i = 0; i < summedDurationsCount; i++)
                {
                    string eventName = reader.ReadString();
                    double eventDuration = reader.ReadDouble();
                    summedDurations.Add(new Tuple<string, double>(eventName, eventDuration));
                }
            }

            int countsCount = reader.ReadInt32();
            if (countsCount > 0)
            {
                counts = new List<Tuple<string, int>>();
                for (int i = 0; i < countsCount; i++)
                {
                    string eventName = reader.ReadString();
                    int eventCount = reader.ReadInt32();
                    counts.Add(new Tuple<string, int>(eventName, eventCount));
                }
            }

            int memoryUsageCount = reader.ReadInt32();
            if (memoryUsageCount > 0)
            {
                memoryUsages = new List<Tuple<string, MemoryUsage>>();
                for (int i = 0; i < memoryUsageCount; i++)
                {
                    string eventName = reader.ReadString();
                    MemoryUsage usage = new MemoryUsage();
                    usage.TotalAllocatedMemoryDelta = reader.ReadInt64();
                    usage.TotalReservedMemoryDelta = reader.ReadInt64();
                    usage.TotalUnusedReservedMemoryDelta = reader.ReadInt64();
                    memoryUsages.Add(new Tuple<string, MemoryUsage>(eventName, usage));
                }
            }

            message = new ParsedMessage(performanceMonitoringEnabled, durations, summedDurations, counts, memoryUsages);
        }

        public void SetDiagnosticMode(bool enabled)
        {
            StateSynchronizationPerformanceParameters.EnablePerformanceReporting = enabled;
        }

        private struct TimeScope : IDisposable
        {
            private Stopwatch stopwatch;

            public TimeScope(Stopwatch stopwatch)
            {
                this.stopwatch = stopwatch;
                stopwatch.Start();
            }

            public void Dispose()
            {
                if (stopwatch != null)
                {
                    stopwatch.Stop();
                    stopwatch = null;
                }
            }
        }

        private class MemoryDelta : IDisposable
        {
            private long startingAllocatedMemory;
            private long startingReservedMemory;
            private long startingUnusedMemory;

            private MemoryUsage memoryUsage = null;

            public MemoryDelta(MemoryUsage memoryUsage)
            {
                if (!Profiler.enabled)
                {
                    UnityEngine.Debug.LogError($"Profiler not enabled, MemoryUsage not supported.");
                    return;
                }

                this.memoryUsage = memoryUsage;
                startingAllocatedMemory = Profiler.GetTotalAllocatedMemoryLong();
                startingReservedMemory = Profiler.GetTotalReservedMemoryLong();
                startingUnusedMemory = Profiler.GetTotalUnusedReservedMemoryLong();
            }

            public void Dispose()
            {
                if (!Profiler.enabled ||
                    memoryUsage == null)
                {
                    UnityEngine.Debug.LogError($"Profiler not enabled or memoryUsage was null, MemoryUsage not in usable state.");
                    return;
                }

                memoryUsage.TotalAllocatedMemoryDelta += Profiler.GetTotalAllocatedMemoryLong() - startingAllocatedMemory;
                memoryUsage.TotalReservedMemoryDelta += Profiler.GetTotalReservedMemoryLong() - startingReservedMemory;
                memoryUsage.TotalUnusedReservedMemoryDelta += Profiler.GetTotalUnusedReservedMemoryLong() - startingUnusedMemory;
            }
        }
    }
}