using System.Text.Json.Serialization;

namespace Silvia.App.Rpc;

// Wire shapes for the RPC surface. Every root type used by a method, event or channel must be listed in
// AppJsonContext, or the NeoAstra generator fails the build (NEORPC009).

public sealed record EmptyRequest;

public sealed record ProjectDto(
    string Id,
    string Name,
    string? Description,
    string CreatedUtc,
    string? LastOpenedUtc,
    string Status);

public sealed record CreateProjectRequest(string Name, string? Description, string? EmbedModel);

public sealed record RenameProjectRequest(string Id, string Name);

public sealed record ProjectIdRequest(string Id);

public sealed record ImportProjectRequest(string FilePath, string Name);

public sealed record ProjectFileDto(string Path);

public sealed record SourceDto(string Id, string Path, string IncludeGlobs, string ExcludeGlobs, bool Enabled);

public sealed record ProjectIdScoped(string ProjectId);

public sealed record AddSourceRequest(string ProjectId, string Path, string? IncludeGlobs, string? ExcludeGlobs);

/// <summary>The result of the folder dialog: <c>picked</c> with a path, <c>canceled</c>, or <c>unavailable</c>.</summary>
public sealed record FolderPickDto(string Status, string? Path);

/// <summary>Whether a project's embedder and reranker are installed. Without the embedder, search is by keyword.</summary>
public sealed record ModelStatusDto(bool CanEmbed, bool CanRerank);

public sealed record SourceIdRequest(string ProjectId, string SourceId);

public sealed record RescanRequest(string ProjectId, string? SourceId);

public sealed record DocumentDto(string DocumentId, string FileName, string Folder, int Chunks);

public sealed record JobSummaryDto(int Pending, int Running, int Done, int Failed, IReadOnlyList<FailureDto> RecentFailures);

public sealed record FailureDto(string Path, string? Error, string? FinishedUtc);

public sealed record ConversationDto(string Id, string Title, string CreatedUtc);

public sealed record ConversationIdRequest(string ProjectId, string ConversationId);

public sealed record MessageDto(string Role, string Content, string CreatedUtc, IReadOnlyList<CitationDto> Sources);

public sealed record CitationDto(string DocumentId, int ChunkIndex, string FileName, string Text);

public sealed record AskRequest(string ProjectId, string? ConversationId, string Question);

public sealed record AskUpdateDto(string Kind, string? ConversationId, string? Text, IReadOnlyList<CitationDto>? Sources);

public sealed record SearchRequest(string ProjectId, string Query, int? TopK);

public sealed record HitDto(string DocumentId, int ChunkIndex, string FileName, string Text, int Relevance);

public sealed record ProjectSettingsDto(
    string EmbedModel,
    int EmbedDims,
    string RerankModel,
    string ChatModel,
    string CloudPolicy,
    int ChunkTokens,
    int ChunkOverlap,
    int TopK,
    int MemoryTopK,
    int MemoryMinPercent,
    string SystemPrompt);

public sealed record UpdateProjectSettingsRequest(string ProjectId, ProjectSettingsDto Settings);

public sealed record ModelDto(string Id, string Kind, int Dimensions, bool Installed, bool Default);

public sealed record GlobalSettingsDto(string DataRoot, string ModelsRoot, bool DeepSeekKeyConfigured, int MaxOpenProjects);

public sealed record AppearanceDto(
    string Theme,
    int InterfaceSize,
    int ReadingSize,
    string LineHeight,
    string ReadingWidth,
    string InterfaceFont,
    string ReadingFont,
    string MonoFont);

public sealed record FontListDto(IReadOnlyList<string> Bundled, IReadOnlyList<string> System);

public sealed record OpenDocumentRequest(string ProjectId, string DocumentId, int? Page);

public sealed record ShellResult(bool Opened, string Message);

public sealed record SetDeepSeekKeyRequest(string Key);

public sealed record ModelDownloadStatusDto(string State, string? File, int Percent, string? Message);

[JsonSerializable(typeof(EmptyRequest))]
[JsonSerializable(typeof(ProjectDto))]
[JsonSerializable(typeof(ProjectDto[]))]
[JsonSerializable(typeof(CreateProjectRequest))]
[JsonSerializable(typeof(RenameProjectRequest))]
[JsonSerializable(typeof(ProjectIdRequest))]
[JsonSerializable(typeof(ImportProjectRequest))]
[JsonSerializable(typeof(ProjectFileDto))]
[JsonSerializable(typeof(SourceDto))]
[JsonSerializable(typeof(SourceDto[]))]
[JsonSerializable(typeof(ProjectIdScoped))]
[JsonSerializable(typeof(AddSourceRequest))]
[JsonSerializable(typeof(FolderPickDto))]
[JsonSerializable(typeof(ModelStatusDto))]
[JsonSerializable(typeof(SourceIdRequest))]
[JsonSerializable(typeof(RescanRequest))]
[JsonSerializable(typeof(DocumentDto))]
[JsonSerializable(typeof(DocumentDto[]))]
[JsonSerializable(typeof(JobSummaryDto))]
[JsonSerializable(typeof(FailureDto))]
[JsonSerializable(typeof(ConversationDto))]
[JsonSerializable(typeof(ConversationDto[]))]
[JsonSerializable(typeof(ConversationIdRequest))]
[JsonSerializable(typeof(MessageDto))]
[JsonSerializable(typeof(MessageDto[]))]
[JsonSerializable(typeof(CitationDto))]
[JsonSerializable(typeof(AskRequest))]
[JsonSerializable(typeof(AskUpdateDto))]
[JsonSerializable(typeof(SearchRequest))]
[JsonSerializable(typeof(HitDto))]
[JsonSerializable(typeof(HitDto[]))]
[JsonSerializable(typeof(ProjectSettingsDto))]
[JsonSerializable(typeof(UpdateProjectSettingsRequest))]
[JsonSerializable(typeof(ModelDto))]
[JsonSerializable(typeof(ModelDto[]))]
[JsonSerializable(typeof(GlobalSettingsDto))]
[JsonSerializable(typeof(AppearanceDto))]
[JsonSerializable(typeof(FontListDto))]
[JsonSerializable(typeof(OpenDocumentRequest))]
[JsonSerializable(typeof(ShellResult))]
[JsonSerializable(typeof(SetDeepSeekKeyRequest))]
[JsonSerializable(typeof(ModelDownloadStatusDto))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class AppJsonContext : JsonSerializerContext;
