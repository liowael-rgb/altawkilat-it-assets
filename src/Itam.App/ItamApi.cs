using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Itam.App;

public static class ItamServices
{
    /// <summary>Registers the store. The host supplies the database connection, IItamSession, IItamSecrets and the PDF font.</summary>
    public static IServiceCollection AddItam(this IServiceCollection services, Func<IServiceProvider, DbConnection> connections, byte[] pdfFont, string? labelBaseUrl = null, byte[]? pdfLatinFont = null, byte[]? pdfLatinBoldFont = null)
    {
        var pdf = new ItamPdf(pdfFont, pdfLatinFont, pdfLatinBoldFont);
        services.AddScoped(sp => new ItamStore(() => connections(sp), sp.GetRequiredService<IItamSecrets>(), sp.GetService<TimeProvider>()) { Pdf = pdf, LabelBaseUrl = labelBaseUrl ?? "https://hub.altawkilat.com/itasm/a/" });
        return services;
    }
}

/// <summary>Pages at the site root and the API under /api/. Every API call checks the person's IT roles on the server.</summary>
public static class ItamApi
{
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    const string Csp = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'";

    static async Task<IResult> Guard(HttpContext ctx, Func<ItamUser, Task<IResult>> action)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        try
        {
            var user = await ctx.RequestServices.GetRequiredService<IItamSession>().Current(ctx, ctx.RequestAborted);
            if (user is null) return Results.Json(new { error = "Sign in with your Windows account." }, statusCode: 401);
            if (!HttpMethods.IsGet(ctx.Request.Method)) await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx);
            return await action(user);
        }
        catch (ItamAccessDenied ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
        catch (ItamConflict ex) { return Results.Json(new { error = ex.Message }, statusCode: 409); }
        catch (AntiforgeryValidationException) { return Results.Json(new { error = "Reload the page and try again." }, statusCode: 400); }
        catch (JsonException) { return Results.Json(new { error = "The request is not valid." }, statusCode: 400); }
        catch (ArgumentException ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
        catch (InvalidDataException) { return Results.Json(new { error = "The upload is too large." }, statusCode: 400); }
    }

    /// <summary>JSON body, or a form with a "data" JSON field and one file.</summary>
    static async Task<(T Data, ItamUpload? File)> Form<T>(HttpRequest request)
    {
        if (!request.HasFormContentType) return (await JsonSerializer.DeserializeAsync<T>(request.Body, Web) ?? throw new ArgumentException("The request is empty."), null);
        var form = await request.ReadFormAsync();
        var data = JsonSerializer.Deserialize<T>(form["data"].ToString(), Web) ?? throw new ArgumentException("The request is empty.");
        if (form.Files.Count > 1) throw new ArgumentException("Attach one file.");
        ItamUpload? file = null;
        if (form.Files.Count == 1)
        {
            var f = form.Files[0];
            if (f.Length > ItamStore.MaxFileBytes) throw new ArgumentException($"{f.FileName}: files must be up to 10 MB.");
            using var ms = new MemoryStream(); await f.CopyToAsync(ms); file = new(f.FileName, f.ContentType, ms.ToArray());
        }
        return (data, file);
    }

    static IResult Pdf(ItamUpload f, bool download = false) => Results.File(f.Content, f.MediaType, download ? f.FileName : null);

    sealed record SentIn(DateOnly? On);

    public static void MapItam(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/me", (HttpContext ctx, ItamStore store, IAntiforgery csrf) => Guard(ctx, async u => Results.Ok(new { me = await store.Me(u, ctx.RequestAborted), csrf = csrf.GetAndStoreTokens(ctx).RequestToken })));
        api.MapGet("/employees", (HttpContext ctx, ItamStore store, string? q) => Guard(ctx, async u => Results.Ok(await store.Employees(u, q, ctx.RequestAborted))));
        api.MapGet("/assets", (HttpContext ctx, ItamStore store, string? q, string? type, string? status, string? region, int? location, int? page,
            string? computerType, string? processor, string? memory, int? model, string? user, string? sap) => Guard(ctx, async u =>
            Results.Ok(await store.Assets(u, new(q, type, status, region, location, page ?? 1, computerType, processor, memory, model, user, sap), ctx.RequestAborted))));
        api.MapGet("/assets/choices", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => Results.Ok(await store.AssetChoices(u, ctx.RequestAborted))));
        api.MapPost("/assets/{id:int}/maintenance", (HttpContext ctx, ItamStore store, int id, ItamMaintenanceInput input) => Guard(ctx, async u => Results.Ok(await store.OpenMaintenance(u, id, input, ctx.RequestAborted))));
        api.MapPost("/maintenance/{id:int}/close", (HttpContext ctx, ItamStore store, int id, ItamMaintenanceClose input) => Guard(ctx, async u => { await store.CloseMaintenance(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapGet("/assets/{id:int}", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => Results.Ok(await store.Asset(u, id, ctx.RequestAborted))));
        api.MapPost("/assets", (HttpContext ctx, ItamStore store, ItamAssetInput input) => Guard(ctx, async u => Results.Ok(await store.Register(u, input, ctx.RequestAborted))));
        api.MapPut("/assets/{id:int}", (HttpContext ctx, ItamStore store, int id, ItamAssetInput input) => Guard(ctx, async u => { await store.Update(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/assets/{id:int}/key/{code}", (HttpContext ctx, ItamStore store, int id, string code) => Guard(ctx, async u => Results.Ok(new { value = await store.ViewKey(u, id, code, ctx.RequestAborted) })));

        api.MapGet("/pos", (HttpContext ctx, ItamStore store, string? q) => Guard(ctx, async u => Results.Ok(await store.Pos(u, q, ctx.RequestAborted))));
        api.MapGet("/pos/{id:int}", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => Results.Ok(await store.Po(u, id, ctx.RequestAborted))));
        api.MapPost("/pos", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => { var (d, f) = await Form<ItamPoInput>(ctx.Request); return Results.Ok(await store.CreatePo(u, d, f, ctx.RequestAborted)); }));
        api.MapPut("/pos/{id:int}", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => { var (d, f) = await Form<ItamPoInput>(ctx.Request); await store.UpdatePo(u, id, d, f, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/pos/{id:int}/receive", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => { var (d, f) = await Form<ItamReceiveInput>(ctx.Request); return Results.Ok(await store.Receive(u, id, d, f, ctx.RequestAborted)); }));
        api.MapGet("/pos/{id:int}/report.pdf", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => Pdf(await store.PoReport(u, id, ctx.RequestAborted))));
        api.MapGet("/pos/{id:int}/pack.pdf", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => Pdf(await store.PoPack(u, id, ctx.RequestAborted))));
        api.MapPost("/pos/{id:int}/prints/{print:int}/sent", (HttpContext ctx, ItamStore store, int id, int print, SentIn input) => Guard(ctx, async u => { await store.MarkSent(u, id, print, input.On, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapGet("/files/{id:long}", (HttpContext ctx, ItamStore store, long id) => Guard(ctx, async u => { var f = await store.File(u, id, ctx.RequestAborted); return Results.File(f.Content, f.MediaType, f.MediaType == "application/pdf" ? null : f.FileName); }));

        // Handover (build step 2).
        api.MapGet("/handover", (HttpContext ctx, ItamStore store, string? status, string? q) => Guard(ctx, async u => Results.Ok(await store.Handover(u, status, q, ctx.RequestAborted))));
        api.MapGet("/assets/{id:int}/handover", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => Results.Ok(await store.AssetHandover(u, id, ctx.RequestAborted))));
        api.MapPost("/assets/{id:int}/deliver", (HttpContext ctx, ItamStore store, int id, ItamDeliveryInput input) => Guard(ctx, async u => Results.Ok(await store.Deliver(u, id, input, ctx.RequestAborted))));
        api.MapGet("/assets/{id:int}/label.pdf", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => Pdf(await store.LabelPdf(u, id, ctx.RequestAborted))));
        api.MapGet("/assets/{id:int}/label.zpl", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => Pdf(await store.LabelZpl(u, id, ctx.RequestAborted), download: true)));
        api.MapGet("/deliveries/{id:int}/note.pdf", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => Pdf(await store.DeliveryNote(u, id, ctx.RequestAborted))));
        api.MapGet("/deliveries/{id:int}/receipt.pdf", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => Pdf(await store.ReceiptNote(u, id, ctx.RequestAborted))));
        api.MapGet("/deliveries/{id:int}/assessment.pdf", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => Pdf(await store.AssessmentNote(u, id, ctx.RequestAborted))));
        api.MapGet("/deliveries/{id:int}/files/{which}", (HttpContext ctx, ItamStore store, int id, string which) => Guard(ctx, async u => { var f = await store.DeliveryFile(u, id, which == "signed" ? "signed" : "evidence", ctx.RequestAborted); return Results.File(f.Content, f.MediaType, f.MediaType == "application/pdf" ? null : f.FileName); }));
        api.MapPost("/deliveries/{id:int}/signed", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => { var (_, f) = await Form<object>(ctx.Request); await store.UploadSigned(u, id, f, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/deliveries/{id:int}/return", (HttpContext ctx, ItamStore store, int id, ItamReturnInput input) => Guard(ctx, async u => Results.Ok(await store.ReturnAsset(u, id, input, ctx.RequestAborted))));
        api.MapPost("/deliveries/{id:int}/assessment", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => { var (d, f) = await Form<ItamAssessmentInput>(ctx.Request); return Results.Ok(await store.Assess(u, id, d, f, ctx.RequestAborted)); }));
        api.MapGet("/tasks", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => Results.Ok(await store.MyTasks(u, ctx.RequestAborted))));
        api.MapPost("/tasks/{id:int}/done", (HttpContext ctx, ItamStore store, int id, ItamTaskDone input) => Guard(ctx, async u => { await store.TaskDone(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));

        // Ink and invoices (build step 3a).
        api.MapGet("/printers", (HttpContext ctx, ItamStore store, string? q) => Guard(ctx, async u => Results.Ok(await store.Printers(u, q, ctx.RequestAborted))));
        api.MapGet("/ink", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => Results.Ok(await store.Ink(u, ctx.RequestAborted))));
        api.MapPost("/ink", (HttpContext ctx, ItamStore store, ItamInkRequestInput input) => Guard(ctx, async u => Results.Ok(await store.CreateInkRequest(u, input, ctx.RequestAborted))));
        api.MapPost("/ink/{id:int}/send", (HttpContext ctx, ItamStore store, int id, ItamStore.ItamSendForApproval input) => Guard(ctx, async u => { await store.SendInkForApproval(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/ink/{id:int}/printer", (HttpContext ctx, ItamStore store, int id, ItamInkPrinter input) => Guard(ctx, async u => { await store.LinkInkPrinter(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/ink/{id:int}/register-printer", (HttpContext ctx, ItamStore store, int id, ItamInkRegisterPrinter input) => Guard(ctx, async u => Results.Ok(await store.RegisterInkPrinter(u, id, input, ctx.RequestAborted))));
        api.MapPost("/ink/{id:int}/decision", (HttpContext ctx, ItamStore store, int id, ItamDecision input) => Guard(ctx, async u => { await store.DecideInk(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/ink/{id:int}/cancel", (HttpContext ctx, ItamStore store, int id, ItamCancelInput input) => Guard(ctx, async u => { await store.CancelInk(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/ink/{id:int}/deliver", (HttpContext ctx, ItamStore store, int id, ItamInkDeliverInput input) => Guard(ctx, async u => Results.Ok(await store.DeliverInk(u, id, input, ctx.RequestAborted))));
        api.MapPost("/orders", (HttpContext ctx, ItamStore store, ItamOrderInput input) => Guard(ctx, async u => Results.Ok(await store.CreateOrder(u, input, ctx.RequestAborted))));
        api.MapGet("/orders/{id:int}/order.pdf", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => Pdf(await store.OrderPdf(u, id, ctx.RequestAborted))));
        api.MapGet("/ink/{id:int}/request.pdf", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => Pdf(await store.InkRequestPdf(u, id, ctx.RequestAborted))));
        api.MapGet("/suppliers", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => Results.Ok(await store.Suppliers(u, ctx.RequestAborted))));
        api.MapGet("/suppliers/{id:int}", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => Results.Ok(await store.Supplier(u, id, ctx.RequestAborted))));
        api.MapPost("/suppliers", (HttpContext ctx, ItamStore store, ItamSupplierInput input) => Guard(ctx, async u => Results.Ok(await store.SaveSupplier(u, null, input, ctx.RequestAborted))));
        api.MapPost("/suppliers/{id:int}", (HttpContext ctx, ItamStore store, int id, ItamSupplierInput input) => Guard(ctx, async u => Results.Ok(await store.SaveSupplier(u, id, input, ctx.RequestAborted))));
        api.MapGet("/invoices", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => Results.Ok(await store.Invoices(u, ctx.RequestAborted))));
        api.MapGet("/invoices/{id:int}", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => Results.Ok(await store.Invoice(u, id, ctx.RequestAborted))));
        api.MapGet("/invoices/{id:int}/file", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => Pdf(await store.InvoiceFile(u, id, ctx.RequestAborted))));
        api.MapPost("/invoices", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => { var (d, f) = await Form<ItamInvoiceInput>(ctx.Request); return Results.Ok(await store.CreateInvoice(u, d, f, ctx.RequestAborted)); }));
        api.MapPost("/packs", (HttpContext ctx, ItamStore store, ItamPackInput input) => Guard(ctx, async u => Results.Ok(await store.CreatePack(u, input, ctx.RequestAborted))));
        api.MapGet("/packs/{id:int}/pack.pdf", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => Pdf(await store.PackPdf(u, id, ctx.RequestAborted))));
        api.MapPost("/packs/{id:int}/dates", (HttpContext ctx, ItamStore store, int id, ItamPackDates input) => Guard(ctx, async u => { await store.PackDates(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));

        api.MapGet("/admin", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => Results.Ok(await store.Admin(u, ctx.RequestAborted))));
        api.MapPost("/admin", (HttpContext ctx, ItamStore store, ItamAdminChange change) => Guard(ctx, async u => { await store.Change(u, change, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapGet("/backups", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => Results.Ok(await store.Backups(u, ctx.RequestAborted))));
        api.MapPost("/backups", (HttpContext ctx, ItamStore store, ItamBackupInput input) => Guard(ctx, async u => { await store.SetBackup(u, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/backups/{id:int}/cancel", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => { await store.CancelBackup(u, id, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));

        // The QR code on the asset label: opens the asset list filtered on the Asset No.
        app.MapGet("/a/{no}", (string no) => Results.Redirect("/?q=" + Uri.EscapeDataString(no)));
        app.MapGet("/", (HttpContext ctx) => Asset(ctx, "index.html"));
        app.MapGet("/{file}", (HttpContext ctx, string file) => Asset(ctx, file));
    }

    static readonly Assembly Assembly = typeof(ItamApi).Assembly;
    static IResult Asset(HttpContext ctx, string file)
    {
        if (file.Contains("..", StringComparison.Ordinal) || file.Contains('/') || file.Contains('\\')) return Results.NotFound();
        var stream = Assembly.GetManifestResourceStream("Itam.App.Assets." + file);
        if (stream is null) return Results.NotFound();
        var type = Path.GetExtension(file) switch { ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8", ".svg" => "image/svg+xml", ".png" => "image/png", _ => "application/octet-stream" };
        ctx.Response.Headers.CacheControl = file.EndsWith(".html", StringComparison.Ordinal) ? "no-store" : "no-cache";
        ctx.Response.Headers["Content-Security-Policy"] = Csp;
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.Stream(stream, type);
    }
}
