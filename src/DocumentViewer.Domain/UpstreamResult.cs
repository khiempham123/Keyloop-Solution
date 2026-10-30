namespace DocumentViewer.Domain;

public abstract record UpstreamResult<T>
{
    private UpstreamResult()
    {
    }

    public sealed record Ok(T Value, TimeSpan Duration) : UpstreamResult<T>;

    public sealed record Failed(UpstreamFailure Failure, TimeSpan Duration) : UpstreamResult<T>;
}
