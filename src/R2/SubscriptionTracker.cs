﻿using Microsoft.Extensions.Logging;
using R2.Internal;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;

namespace R2;

public static class SubscriptionTracker
{
    static int trackingIdCounter = 0;

    // TODO: UnityEditor? AppSwitch?
    public static bool EnableAutoReload = true;
    public static bool EnableTracking = true;
    public static bool EnableStackTrace = true;
    // TODO: EnableStackTraceFileLink

    static readonly WeakDictionary<TrackableDisposable, TrackingState> tracking = new();

    // for iterationg
    static List<TrackingState> iterateCache = new();

    // flag for polling performance
    static bool dirty;

    internal static bool TryTrackActiveSubscription(IDisposable subscription, int skipFrame, [NotNullWhen(true)] out TrackableDisposable? trackableDisposable)
    {
        if (!EnableTracking)
        {
            trackableDisposable = default;
            return false;
        }

        dirty = true;

        string stackTrace = "";
        if (EnableStackTrace)
        {
            var trace = new StackTrace(skipFrame, true);
            stackTrace = trace.ToString();
        }

        var unwrappedSubscription = UnwrapTrackableDisposable(subscription);
        string typeName;
        if (EnableStackTrace)
        {
            var sb = new StringBuilder();
            TypeBeautify(unwrappedSubscription.GetType(), sb);
            typeName = sb.ToString();
        }
        else
        {
            typeName = unwrappedSubscription.GetType().Name;
        }

        var id = Interlocked.Increment(ref trackingIdCounter);
        trackableDisposable = new TrackableDisposable(subscription, id);
        EventSystem.Logger.AddTracking(id);
        tracking.TryAdd(trackableDisposable, new TrackingState(id, typeName, DateTime.Now, stackTrace)); // use local now.

        return true;
    }

    internal static void RemoveTracking(TrackableDisposable subscription)
    {
        if (!EnableTracking) return;

        dirty = true;
        tracking.TryRemove(subscription);
    }

    public static bool CheckAndResetDirty()
    {
        var current = dirty;
        dirty = false;
        return current;
    }

    public static void ForEachActiveTask(Action<TrackingState> action)
    {
        lock (iterateCache)
        {
            var count = tracking.CaptureSnapshot(ref iterateCache, clear: false);
            var span = CollectionsMarshal.AsSpan(iterateCache).Slice(0, count);
            span.Sort();
            try
            {
                for (int i = 0; i < count; i++)
                {
                    action(iterateCache[i]);
                }
            }
            finally
            {
                span.Clear();
            }
        }
    }

    static void TypeBeautify(Type type, StringBuilder sb)
    {
        if (type.IsNested)
        {
            // TypeBeautify(type.DeclaringType, sb);
            sb.Append(type.DeclaringType!.Name.ToString());
            sb.Append(".");
        }

        if (type.IsGenericType)
        {
            var genericsStart = type.Name.IndexOf("`");
            if (genericsStart != -1)
            {
                sb.Append(type.Name.Substring(0, genericsStart));
            }
            else
            {
                sb.Append(type.Name);
            }
            sb.Append("<");
            var first = true;
            foreach (var item in type.GetGenericArguments())
            {
                if (!first)
                {
                    sb.Append(", ");
                }
                first = false;
                TypeBeautify(item, sb);
            }
            sb.Append(">");
        }
        else
        {
            sb.Append(type.Name);
        }
    }

    static IDisposable UnwrapTrackableDisposable(IDisposable disposable)
    {
        while (disposable is TrackableDisposable t)
        {
            disposable = t.Disposable;
        }
        return disposable;
    }
}

internal sealed class TrackableDisposable(IDisposable disposable, int trackingId) : IDisposable
{
    public IDisposable Disposable => disposable;
    public int TrackingId => trackingId;
    int disposed;

    public void Dispose()
    {
        var field = Interlocked.CompareExchange(ref disposed, 1, 0);
        if (field == 0)
        {
            EventSystem.Logger.RemoveTracking(trackingId);
            SubscriptionTracker.RemoveTracking(this);
        }

        disposable.Dispose();
    }

    public override string? ToString()
    {
        return disposable.ToString();
    }
}

public record struct TrackingState(int TrackingId, string FormattedType, DateTime AddTime, string StackTrace) : IComparable<TrackingState>
{
    public int CompareTo(TrackingState other)
    {
        return TrackingId.CompareTo(other.TrackingId);
    }
}