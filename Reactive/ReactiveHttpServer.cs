using DTO;
using System.Net;
using System.Reactive;
using System.Reactive.Concurrency;
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

    private readonly Subject<Unit> _exitSubject = new();

    private readonly Subject<ContextTopicDTO> _sendResponseSubject = new();

    private ReactiveGitHubSearchClient _gitHubSearchClient;

    private ReactiveHttpListener _httpListener;

    public ReactiveHttpServer(
        string gitHubPAT,
        int resultsPerPage,
        string[] prefixes,
        IScheduler? listeningScheduler = null)
    {
        _gitHubSearchClient = new ReactiveGitHubSearchClient(gitHubPAT, resultsPerPage);
        _httpListener = new ReactiveHttpListener(prefixes, listeningScheduler ?? TaskPoolScheduler.Default);
    }

    public void Launch()
    {
        var httpListenerSub = _httpListener
            .TakeUntil(_exitSubject)
            // Logging request info
            .Do(context =>
            {
                string requestLog = string.Format(
                    "{0} {1} HTTP/{2}\nHost: {3}\nUser-agent: {4}\n",
                    context.Request.HttpMethod,
                    context.Request.RawUrl,
                    context.Request.ProtocolVersion,
                    context.Request.UserHostName,
                    context.Request.UserAgent
                );

                Console.WriteLine($"REQUEST:\n{requestLog}\tReceived on thread {Thread.CurrentThread.ManagedThreadId}\n====================================\n");
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
                    _sendResponseSubject.OnNext(contextTopicDto);
            })
            .SubscribeOn(new EventLoopScheduler())
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

        var sendResponseSub = _sendResponseSubject
            .TakeUntil(_exitSubject)
            .SelectMany(contextTopicDto =>
            {
                return _gitHubSearchClient.SearchReposByTopic(contextTopicDto.Topic)
                    .Select(searchResult =>
                    {
                        List<Repo> repos = new();
                        foreach (var repo in searchResult.Items)
                        {
                            repos.Add(new Repo
                            {
                                Name = repo.Name,
                                Description = repo.Description,
                                Url = repo.Url,
                                StarsCount = repo.StargazersCount,
                                ForksCount = repo.ForksCount,
                            });
                        }
                        return new ContextReposDTO
                        {
                            Context = contextTopicDto.Context,
                            Repos = repos
                        };
                    });
            })
            .SubscribeOn(Scheduler.Default)
            .Subscribe(
                onNext: contextReposDto =>
                {
                    var context = contextReposDto.Context;
                    var repos = contextReposDto.Repos;
                    var responseBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(repos));

                    string responseLog = string.Format(
                        "Status: {0}\nDate: {1}\nContent-Type: {2}\nContent-Length: {3}\n",
                        context.Response.StatusCode,
                        DateTime.Now,
                        context.Response.ContentType,
                        responseBody.Length
                    );

                    Console.WriteLine($"RESPONSE:\n{responseLog}\tSent on thread {Thread.CurrentThread.ManagedThreadId}\n====================================\n");

                    _ = SendResponse(context, responseBody, "application/json");
                },
                onError: ex => HandleError(ex)
            );
    }

    public void Terminate()
    {
        _exitSubject.OnNext(Unit.Default);
        _exitSubject.OnCompleted();

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
        HttpStatusCode statusCode = HttpStatusCode.OK
    )
    {
        context.Response.ContentType = contentType;
        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentLength64 = responseBody.Length;
        using (var outputStream = context.Response.OutputStream)
            await outputStream.WriteAsync(responseBody, 0, responseBody.Length);
    }
}