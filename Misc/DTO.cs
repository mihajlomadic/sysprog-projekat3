using System.Net;

namespace DTO;

public class ContextTopicDTO
{
    public required HttpListenerContext Context { get; set; }
    public required string Topic { get; set; }
}

public class Repo
{
    public required string Name { get; set; }
    public required string Description { get; set; }
    public required string Url { get; set; }
    public required int StarsCount { get; set; }
    public required int ForksCount { get; set; }
}

public class ContextReposDTO
{
    public required HttpListenerContext Context { get; set; }
    public required List<Repo> Repos { get; set; }
}