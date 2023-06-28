using Caching;
using DTO;
using System.Net;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using System.Web;

namespace Reactive;

public class ReactiveHttpServer
{

    public static readonly byte[] BadRequestBody
        = Encoding.ASCII.GetBytes("<h1>Bad request.</h1>");
    public static readonly byte[] MethodNotAllowed
        = Encoding.ASCII.GetBytes("<h1>Method not allowed.</h1>");

    private readonly object _consoleWriteLock = new();

    private readonly Subject<Unit> _terminator = new();

    private readonly ISubject<ContextTopicDTO> _handler = new Subject<ContextTopicDTO>();

    private ReactiveGitHubSearchClient _gitHubClient;

    private ReactiveHttpListener _listener;

    private ReaderWriterLRUCache<string, List<Repo>> _cache;

    public ReactiveHttpServer(
        string gitHubPAT,
        int resultsPerPage,
        string[] prefixes,
        IScheduler? listeningScheduler = null,
        IScheduler? handlingScheduler = null)
    {
        _gitHubClient = new ReactiveGitHubSearchClient(gitHubPAT, resultsPerPage);
        _listener = new ReactiveHttpListener(prefixes, listeningScheduler ?? Scheduler.Default);
        _handler.ObserveOn(handlingScheduler ?? TaskPoolScheduler.Default);
        _cache = new ReaderWriterLRUCache<string, List<Repo>>(30);
    }

    public IDisposable Launch()
    {
        var httpListenerSub = _listener
            .TakeUntil(_terminator)
            // Thread info
            .Do(context =>
            {
                Console.WriteLine($"Got request on thread: {Thread.CurrentThread.ManagedThreadId}");
            })
            // Request validation 
            // - Request method must be GET
            // - Request must have query string, with `topic` parameter
            .Select(context =>
            {
                if (context.Request.HttpMethod != "GET")
                {
                    _ = SendResponse(context, MethodNotAllowed,
                            "text/html", HttpStatusCode.MethodNotAllowed);
                    return null;
                }

                Uri? requestUri = context.Request.Url;

                if (requestUri == null)
                {
                    _ = SendResponse(context, BadRequestBody,
                            "text/html", HttpStatusCode.BadRequest);
                    return null;
                }

                var requestQuery = HttpUtility.ParseQueryString(requestUri.Query);

                if (requestQuery.Count == 0)
                {
                    _ = SendResponse(context, BadRequestBody,
                            "text/html", HttpStatusCode.BadRequest);
                    return null;
                }

                var topic = requestQuery.Get("topic");

                if (topic == null)
                {

                    _ = SendResponse(context, BadRequestBody,
                            "text/html", HttpStatusCode.BadRequest);
                    return null;
                }

                return new ContextTopicDTO
                {
                    Context = context,
                    Topic = topic
                };
            })
            // Transfering context to another observable (subject)
            // which will search GitHub for repos by topic
            // and send response
            .Do(contextTopicDto =>
            {
                if (contextTopicDto != null)
                    _handler.OnNext(contextTopicDto);
            })
            .Subscribe(
                onNext: _ => { },
                onError: ex => HandleError(ex),
                onCompleted: () =>
                {
                    lock (_consoleWriteLock)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"\nServer stopped listening on thread: {Thread.CurrentThread.ManagedThreadId}");
                        Console.ResetColor();
                    }
                }
            );

        var sendResponseSub = _handler
            .TakeUntil(_terminator)
            .Do(_ => Console.WriteLine($"Handling request on thread: {Thread.CurrentThread.ManagedThreadId}"))
            .SelectMany(contextTopicDto =>
            {
                List<Repo>? repos = null;
                if (_cache.TryRead(contextTopicDto.Topic, out repos))
                {
                    return Observable.Return(new ContextReposDTO
                    {
                        Context = contextTopicDto.Context,
                        Repos = repos
                    });
                }
                else
                {
                    return _gitHubClient.SearchReposByTopic(contextTopicDto.Topic)
                        .Select(searchResult => 
                        {
                            return searchResult.Items
                                .AsParallel()
                                // .WithDegreeOfParallelism(Environment.ProcessorCount)
                                .Select(repo => new Repo
                                {
                                    Name = repo.Name,
                                    Description = repo.Description,
                                    Url = repo.Url,
                                    StarsCount = repo.StargazersCount,
                                    ForksCount = repo.ForksCount,
                                })
                                .ToList();
                        })
                        .Select(processedRepos =>
                        {
                            // caching repos for topic
                            _cache.Write(contextTopicDto.Topic, processedRepos);
                            return new ContextReposDTO
                            {
                                Context = contextTopicDto.Context,
                                Repos = processedRepos
                            };
                        });
                }
            })
            .Subscribe(
                onNext: contextReposDto =>
                {
                    var context = contextReposDto.Context;
                    var repos = contextReposDto.Repos;
                    var responseBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(repos));

                    _ = SendResponse(context, responseBody, "application/json");
                },
                onError: ex => HandleError(ex)
            );

        return Disposable.Create(() =>
        {
            httpListenerSub.Dispose();
            sendResponseSub.Dispose();
        });
    }

    public void Terminate()
    {
        _terminator.OnNext(Unit.Default);
        _terminator.OnCompleted();

        lock (_consoleWriteLock)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\nServer terminated on thread: {Thread.CurrentThread.ManagedThreadId}");
            Console.ResetColor();
        }
    }

    private void HandleError(Exception ex, ConsoleColor color = ConsoleColor.Red)
    {
        StringBuilder sb = new();

        sb.AppendLine($"Error on thread {Thread.CurrentThread.ManagedThreadId}!");
        sb.AppendLine($"Error message: {ex.Message}");
        sb.AppendLine($"Error stack trace: {ex.StackTrace}");
        sb.AppendLine($"Error inner exception: {ex.InnerException?.Message ?? "No inner exception"}");

        lock (_consoleWriteLock)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(sb.ToString());
            Console.ResetColor();
        }
    }

    private async Task SendResponse(
        HttpListenerContext context,
        byte[] responseBody,
        string contentType,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        context.Response.ContentType = contentType;
        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentLength64 = responseBody.Length;
        using (var outputStream = context.Response.OutputStream)
            await outputStream.WriteAsync(responseBody, 0, responseBody.Length);
        Log(context);
    }

    private void Log(HttpListenerContext context)
    {
        string requestLog = string.Format(
            "{0} {1} HTTP/{2}\nHost: {3}\nUser-agent: {4}\n",
            context.Request.HttpMethod,
            context.Request.RawUrl,
            context.Request.ProtocolVersion,
            context.Request.UserHostName,
            context.Request.UserAgent
        );

        string responseLog = string.Format(
            "Status: {0}\nDate: {1}\nContent-Type: {2}\nContent-Length: {3}\n",
            context.Response.StatusCode,
            DateTime.Now,
            context.Response.ContentType,
            context.Response.ContentLength64
        );

        StringBuilder sb = new StringBuilder();

        sb.Append("\n====================================\n");
        sb.Append($"Handle started on thread {Thread.CurrentThread.ManagedThreadId}\n");
        sb.Append($"REQUEST:\n{requestLog}\nRESPONSE:\n{responseLog}");
        sb.Append($"API calls left: {_gitHubClient.GetApiInfo().RateLimit.Remaining}\n");
        sb.Append("\n====================================\n");

        Console.WriteLine(sb.ToString());
        Console.Out.Flush();
    }
}