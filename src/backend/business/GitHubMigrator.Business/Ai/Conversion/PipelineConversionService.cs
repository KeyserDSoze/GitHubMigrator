using System.Text.RegularExpressions;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

// The application service behind "propose a conversion with AI". It reads a pipeline, checks the AI connection (verified,
// host allowed, key present, source consent given) BEFORE reading any source text, converts, and returns a proposal.
// It never saves, approves or publishes anything. (The preview reader still fetches the file once to count its tasks and
// drops the text; what this service adds is its own read, and only after consent.)
public sealed class PipelineConversionService(
    IConnectionRepository connections,
    IConnectionSecretStore secrets,
    IAzureWorkloadReader source,
    AiConnectionService ai,
    PipelineConverter converter)
{
    private static readonly Regex SimpleKey = new(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,99}$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public async Task<ConversionProposal> ConvertAsync(Guid sourceId, string projectId, int pipelineId, Guid aiConnectionId,
        ConversionOptions? options, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(projectId) || !SimpleKey.IsMatch(projectId)) throw new WorkloadInputException("InvalidProjectId");
        if (pipelineId < 1) throw new WorkloadInputException("InvalidPipelineId");

        var connection = await connections.GetAsync(sourceId, ct) ?? throw new WorkloadInputException("SourceNotFound");
        if (connection.Direction != ConnectionDirection.Source || !ProviderKeys.IsAzureDevOpsSource(connection.ProviderKey))
            throw new WorkloadInputException("UnsupportedSource");
        var token = await secrets.ReadAsync(connection.SecretReference, ct);
        if (string.IsNullOrEmpty(token)) throw new WorkloadInputException("SourceCredentialMissing");

        var reader = WorkloadSources.For(source, connection);
        var pipeline = await reader.GetPipelineAsync(connection.Organization, projectId, pipelineId, token, ct);

        AiConnectionUse use;
        try { use = await ai.ResolveAsync(aiConnectionId, requireSourceConsent: true, ct); }
        catch (AiConnectionException ex)
        {
            // no consent, an unverified model or a disallowed host: the converter does not read the source text
            var reason = ex.Code == "SourceConsentRequired" ? ex.Code : "AiConnection" + ex.Code;
            return ConversionProposal.NotAttempted(reason, ConversionPlanBuilder.Build(pipeline, null));
        }

        string? yaml = null;
        if (pipeline.Kind == "yaml")
        {
            var yamlReader = reader as IPipelineYamlReader ?? throw new WorkloadInputException("UnsupportedSource");
            var read = await yamlReader.ReadPipelineYamlAsync(connection.Organization, projectId, pipelineId, token, ct);
            if (read.TooLarge) return ConversionProposal.NotAttempted("SourceTooLarge", ConversionPlanBuilder.Build(pipeline, null));
            yaml = read.Content;
        }
        return await converter.ConvertAsync(use, pipeline, yaml, options, ct);
    }
}
