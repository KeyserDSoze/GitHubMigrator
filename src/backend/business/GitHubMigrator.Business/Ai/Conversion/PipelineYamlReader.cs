namespace GitHubMigrator.Business;

// The raw YAML of one pipeline, for the AI conversion only. The preview reader (IAzureWorkloadReader) deliberately never
// returns it; this is a separate, narrow port so the two guarantees stay independent: the preview and every API result
// still never carry source text, and only the converter, which redacts before any model call, may read it.
public sealed record PipelineYaml(string? Content, bool TooLarge);

public interface IPipelineYamlReader
{
    Task<PipelineYaml> ReadPipelineYamlAsync(string organization, string projectId, int pipelineId, string token, CancellationToken ct);
}
