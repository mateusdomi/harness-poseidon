using Harness.Host.Profiles;
using Harness.Persistence.Abstractions.Identity;
using Harness.SharedKernel.Time;

namespace Harness.Host.Leadership;

public static class LeadershipProfileEndpoints
{
    private const long MaxPhotoBytes = 5 * 1024 * 1024;

    public static IEndpointRouteBuilder MapLeadershipProfile(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/leadership-profile").WithTags("leadership-profile");
        group.MapGet("/", GetAsync).Produces<LeadershipProfileRecord>().ProducesProblem(401);
        group.MapPut("/", PutAsync)
            .Accepts<LeadershipProfileWriteRequest>("application/json")
            .Produces<LeadershipProfileRecord>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(409);
        group.MapPost("/photo", UploadPhotoAsync)
            .DisableAntiforgery()
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<LeadershipProfileRecord>()
            .ProducesProblem(400).ProducesProblem(401);
        group.MapGet("/photo", GetPhotoAsync).ProducesProblem(404);
        group.MapPost("/agents/{alias}/photo", UploadAgentPhotoAsync)
            .DisableAntiforgery()
            .Accepts<IFormFile>("multipart/form-data")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(400).ProducesProblem(401);
        group.MapGet("/agents/{alias}/photo", GetAgentPhoto)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(400);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        HttpRequest request, ILocalProfileStore profiles, LeadershipProfileStore store,
        CancellationToken token)
    {
        if (await LocalProfileSession.ResolveAsync(request, profiles, token) is null)
            return Problem(401, "sessao_local_obrigatoria", "É necessário iniciar uma sessão de perfil local.");
        try
        {
            return Results.Ok(await store.ReadAsync(token));
        }
        catch (LeadershipProfileInvalidException)
        {
            return Problem(409, "perfil_de_lideranca_invalido",
                "O arquivo local de personalização está inválido e foi preservado para recuperação.");
        }
    }

    private static async Task<IResult> PutAsync(
        LeadershipProfileWriteRequest? body, HttpRequest request, ILocalProfileStore profiles,
        LeadershipProfileStore store, IClock clock, CancellationToken token)
    {
        if (body is null) return Problem(400, "corpo_obrigatorio", "Informe os dados do perfil.");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
            return Problem(401, "sessao_local_obrigatoria", "É necessário iniciar uma sessão de perfil local.");
        try
        {
            return Results.Ok(await store.UpdateAsync(body, profile.Id, clock.UtcNow, token));
        }
        catch (LeadershipProfileValidationException exception)
        {
            return Problem(400, "perfil_de_lideranca_invalido", exception.Message);
        }
        catch (LeadershipProfileConflictException)
        {
            return Problem(409, "versao_do_perfil_desatualizada",
                "O perfil foi alterado em outra ação. Recarregue os dados e tente novamente.");
        }
        catch (LeadershipProfileInvalidException)
        {
            return Problem(409, "perfil_de_lideranca_invalido",
                "O arquivo local de personalização está inválido e foi preservado para recuperação.");
        }
    }

    private static async Task<IResult> UploadPhotoAsync(
        IFormFile? photo, HttpRequest request, ILocalProfileStore profiles,
        LeadershipProfileStore store, IClock clock, CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
            return Problem(401, "sessao_local_obrigatoria", "É necessário iniciar uma sessão de perfil local.");
        if (photo is null || photo.Length is <= 0 or > MaxPhotoBytes)
            return Problem(400, "foto_invalida", "Envie uma foto JPG, PNG ou WebP de até 5 MB.");
        var extension = PhotoExtension(photo.ContentType);
        if (extension is null)
            return Problem(400, "formato_de_foto_invalido", "Use uma imagem JPG, PNG ou WebP.");
        await using var content = photo.OpenReadStream();
        return Results.Ok(await store.SavePhotoAsync(
            content, extension, profile.Id, clock.UtcNow, token));
    }

    private static IResult GetPhotoAsync(LeadershipProfileStore store)
    {
        var path = store.ResolvePhotoPath();
        if (path is null)
            return Problem(404, "foto_nao_configurada", "Nenhuma foto personalizada foi configurada.");
        return Results.File(path, ContentType(path), enableRangeProcessing: true);
    }

    private static async Task<IResult> UploadAgentPhotoAsync(
        string alias, IFormFile? photo, HttpRequest request, ILocalProfileStore profiles,
        LeadershipProfileStore store, CancellationToken token)
    {
        if (await LocalProfileSession.ResolveAsync(request, profiles, token) is null)
            return Problem(401, "sessao_local_obrigatoria", "É necessário iniciar uma sessão de perfil local.");
        if (photo is null || photo.Length is <= 0 or > MaxPhotoBytes)
            return Problem(400, "foto_invalida", "Envie uma foto JPG, PNG ou WebP de até 5 MB.");
        var extension = PhotoExtension(photo.ContentType);
        if (extension is null)
            return Problem(400, "formato_de_foto_invalido", "Use uma imagem JPG, PNG ou WebP.");
        try
        {
            await using var content = photo.OpenReadStream();
            await store.SaveAgentPhotoAsync(alias, content, extension, token);
            return Results.NoContent();
        }
        catch (LeadershipProfileValidationException exception)
        {
            return Problem(400, "identidade_de_agente_invalida", exception.Message);
        }
    }

    private static IResult GetAgentPhoto(string alias, LeadershipProfileStore store)
    {
        try
        {
            var path = store.ResolveAgentPhotoPath(alias);
            if (path is null)
                return Results.NoContent();
            return Results.File(path, ContentType(path), enableRangeProcessing: true);
        }
        catch (LeadershipProfileValidationException exception)
        {
            return Problem(400, "identidade_de_agente_invalida", exception.Message);
        }
    }

    private static string? PhotoExtension(string contentType) =>
        contentType.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => null,
        };

    private static string ContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg",
        };

    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);
}
