﻿using System.Runtime.CompilerServices;

namespace R3;

public interface IEventPublisher<TMessage, TComplete>
{
    void PublishOnNext(TMessage message);
    void PublishOnCompleted(TComplete complete);
}

public sealed class Publisher<TMessage, TComplete> : Event<TMessage, TComplete>, IEventPublisher<TMessage, TComplete>, IDisposable
{
    int calledCompleted = 0;
    TComplete? completeValue;
    FreeListCore<_CompletablePublisher> list;
    readonly object completedLock = new object();

    public Publisher()
    {
        list = new FreeListCore<_CompletablePublisher>(this);
    }

    public void PublishOnNext(TMessage message)
    {
        if (list.IsDisposed) ThrowDisposed();
        if (Volatile.Read(ref calledCompleted) != 0) return;

        foreach (var subscriber in list.AsSpan())
        {
            if (subscriber != null)
            {
                subscriber.OnNext(message);
            }
        }
    }

    public void PublishOnErrorResume(Exception error)
    {
        if (list.IsDisposed) ThrowDisposed();
        if (Volatile.Read(ref calledCompleted) != 0) return;

        foreach (var subscriber in list.AsSpan())
        {
            if (subscriber != null)
            {
                subscriber.OnErrorResume(error);
            }
        }
    }

    public void PublishOnCompleted(TComplete complete)
    {
        if (list.IsDisposed) ThrowDisposed();
        if (Volatile.Read(ref calledCompleted) != 0) return;

        // need lock for Subscribe after OnCompleted
        lock (completedLock)
        {
            completeValue = complete;
            calledCompleted = 1;
        }

        foreach (var subscriber in list.AsSpan())
        {
            if (subscriber != null)
            {
                subscriber.OnCompleted(complete);
            }
        }
    }

    protected override IDisposable SubscribeCore(Subscriber<TMessage, TComplete> subscriber)
    {
        if (list.IsDisposed) ThrowDisposed();

        lock (completedLock)
        {
            if (Volatile.Read(ref calledCompleted) != 0)
            {
                subscriber.OnCompleted(completeValue!);
                return Disposable.Empty;
            }

            // need lock after Add
            var subscription = new _CompletablePublisher(this, subscriber);
            subscription.removeKey = list.Add(subscription); // when disposed, may throw DisposedException in this line
            return subscription;
        }
    }

    void Unsubscribe(_CompletablePublisher subscription)
    {
        list.Remove(subscription.removeKey);
    }

    public void Dispose()
    {
        list.Dispose();
    }

    static void ThrowDisposed()
    {
        throw new ObjectDisposedException("CompletablePublisher");
    }

    sealed class _CompletablePublisher(Publisher<TMessage, TComplete>? parent, Subscriber<TMessage, TComplete> subscriber) : IDisposable
    {
        public int removeKey;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnNext(TMessage message)
        {
            subscriber.OnNext(message);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnErrorResume(Exception error)
        {
            subscriber.OnErrorResume(error);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnCompleted(TComplete complete)
        {
            subscriber.OnCompleted(complete);
        }

        public void Dispose()
        {
            var p = Interlocked.Exchange(ref parent, null);
            if (p == null) return;

            p.Unsubscribe(this);
        }
    }
}
