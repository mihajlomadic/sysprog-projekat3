using Octokit;
using Octokit.Reactive;

namespace Reactive;

internal class ReactiveGitHubSearchClient
{

    private GitHubClient _gitHubClient;
    private ObservableGitHubClient _observableGitHubClient;

    private int perPage = 1000;

    public ReactiveGitHubSearchClient(string? personalAccessToken, int perPage = 100)
    {
        if (personalAccessToken == null)
            throw new ArgumentNullException("GitHub PAT is null!");

        _gitHubClient = new(new ProductHeaderValue("sysprog-projekat3"))
        {
            Credentials = new Credentials(personalAccessToken)
        };

        _observableGitHubClient = new(_gitHubClient);
    }

    public IObservable<SearchRepositoryResult> SearchReposByTopic(string topic)
        => _observableGitHubClient.Search.SearchRepo(new SearchRepositoriesRequest
        {
            PerPage = perPage,
            Topic = topic,
            Order = SortDirection.Descending,
            SortField = RepoSearchSort.Stars
        });

    public ApiInfo GetApiInfo()
        => _gitHubClient.GetLastApiInfo();
}
