namespace Severino.Core.Routes;

/// <summary>
/// Default ports of programs that do not speak HTTP. A route to one of them never opens in a
/// browser, so the route form suggests a service instead.
/// </summary>
public static class KnownPorts
{
    private static readonly Dictionary<int, string> NonHttp = new()
    {
        [1433] = "SQL Server",
        [1521] = "Oracle",
        [2181] = "ZooKeeper",
        [3306] = "MySQL",
        [4222] = "NATS",
        [5432] = "PostgreSQL",
        [5672] = "RabbitMQ",
        [6379] = "Redis",
        [9042] = "Cassandra",
        [9092] = "Kafka",
        [11211] = "Memcached",
        [27017] = "MongoDB",
    };

    /// <summary>"PostgreSQL" for 5432; null for a port that may well be a web app.</summary>
    public static string? NonHttpProgram(int port) => NonHttp.GetValueOrDefault(port);
}
