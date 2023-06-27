using System.Net;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;

namespace Reactive;

public class ReactiveHttpListener : IObservable<HttpListenerContext>
{
    private readonly HttpListener _listener;
    private readonly IScheduler _scheduler;

    public ReactiveHttpListener(string[] prefixes, IScheduler scheduler)
    {
        _listener = new HttpListener();
        foreach (string prefix in prefixes)
            _listener.Prefixes.Add(prefix);
        _scheduler = scheduler;
    }

    public IDisposable Subscribe(IObserver<HttpListenerContext> observer)
    {
        _listener.Start();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\nServer started listening on thread: {Thread.CurrentThread.ManagedThreadId}");
        Console.WriteLine($"[\n\t{string.Join("\n\t", _listener.Prefixes)}\n];");
        Console.ResetColor();

        var _ = ObserveContextAsync(observer);

        return Disposable.Create(() =>
        {
            _listener.Stop();
            _listener.Close();
        });
    }

    private async Task ObserveContextAsync(IObserver<HttpListenerContext> observer)
    {
        try
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context = await _listener.GetContextAsync();
                _scheduler.Schedule(() => observer.OnNext(context));
            }
        }
        catch (Exception ex)
        {
            observer.OnError(ex);
        }
        finally
        {
            observer.OnCompleted();
        }
    }
}