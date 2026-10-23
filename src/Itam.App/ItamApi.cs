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
        services.AddHostedService<ItamRequestTimer>();
        services.AddHostedService<ItamMailSender>();
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
            if (user is null) return Results.Json(new { error = "Sign in first." }, statusCode: 401);
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
        api.MapGet("/my", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => Results.Ok(await store.MyItAssets(u, ctx.RequestAborted))));
        api.MapGet("/requests", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => Results.Ok(await store.Requests(u, ctx.RequestAborted))));
        api.MapGet("/requests/find", (HttpContext ctx, ItamStore store, string? no) => Guard(ctx, async u => Results.Ok(await store.RequestByNo(u, no, ctx.RequestAborted))));
        api.MapGet("/requests/{id:int}", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => Results.Ok(await store.Request(u, id, ctx.RequestAborted))));
        api.MapPost("/requests", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => { var (d, f) = await Form<ItamRequestInput>(ctx.Request); return Results.Ok(await store.CreateRequest(u, d, f, ctx.RequestAborted)); }));
        api.MapPost("/requests/{id:int}/decision", (HttpContext ctx, ItamStore store, int id, ItamDecision input) => Guard(ctx, async u => { await store.DecideRequest(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/requests/{id:int}/review", (HttpContext ctx, ItamStore store, int id, ItamRequestReview input) => Guard(ctx, async u => { await store.ReviewRequest(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/requests/{id:int}/cost", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => { var (d, f) = await Form<ItamCostInput>(ctx.Request); await store.SubmitCost(u, id, d, f, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/requests/{id:int}/cost-decision", (HttpContext ctx, ItamStore store, int id, ItamDecision input) => Guard(ctx, async u => { await store.DecideCost(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/requests/{id:int}/ordered", (HttpContext ctx, ItamStore store, int id, ItamRequestOrdered input) => Guard(ctx, async u => { await store.MarkOrdered(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/requests/{id:int}/cash", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => { var (d, f) = await Form<ItamCashPurchase>(ctx.Request); await store.RecordCashPurchase(u, id, d, f, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/requests/{id:int}/fulfil", (HttpContext ctx, ItamStore store, int id, ItamRequestFulfil input) => Guard(ctx, async u => { await store.FulfilRequest(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/requests/{id:int}/cancel", (HttpContext ctx, ItamStore store, int id, ItamCancelInput input) => Guard(ctx, async u => { await store.CancelRequest(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/requests/{id:int}/accounting", (HttpContext ctx, ItamStore store, int id, ItamRequestDate input) => Guard(ctx, async u => { await store.RequestAccounting(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapGet("/requests/{id:int}/document.pdf", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => Pdf(await store.RequestPdf(u, id, ctx.RequestAborted))));
        api.MapGet("/requests/{id:int}/pack.pdf", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => Pdf(await store.RequestPack(u, id, ctx.RequestAborted))));
        api.MapGet("/requests/{id:int}/files/{which}", (HttpContext ctx, ItamStore store, int id, string which) => Guard(ctx, async u => { var f = await store.RequestFile(u, id, which, ctx.RequestAborted); return Results.File(f.Content, f.MediaType, f.FileName); }));
        api.MapGet("/movements", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => Results.Ok(await store.Movements(u, ctx.RequestAborted))));
        api.MapGet("/quick-decision", (HttpContext ctx, ItamStore store, string? k, int id, string? s) => Guard(ctx, async u => Results.Ok(await store.QuickDecisionInfo(u, k, id, s, ctx.RequestAborted))));
        api.MapPost("/quick-decision", (HttpContext ctx, ItamStore store, ItamQuickDecision input) => Guard(ctx, async u => Results.Ok(await store.QuickDecide(u, input, ctx.RequestAborted))));
        api.MapGet("/dashboard", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => Results.Ok(await store.Dashboard(u, ctx.RequestAborted))));
        api.MapGet("/mail", (HttpContext ctx, ItamStore store, string? status) => Guard(ctx, async u => Results.Ok(await store.MailOutbox(u, status, ctx.RequestAborted))));
        api.MapGet("/mail/{id:long}/html", (HttpContext ctx, ItamStore store, long id) => Guard(ctx, async u =>
        {
            var html = await store.MailHtml(u, id, ctx.RequestAborted);
            // The email as it was sent, in a sandboxed frame of the outbox page: its own inline styles, no scripts, no forms.
            ctx.Response.Headers["Content-Security-Policy"] = "sandbox allow-popups allow-popups-to-escape-sandbox; default-src 'none'; style-src 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'self'";
            return Results.Content(html, "text/html; charset=utf-8");
        }));
        api.MapPost("/mail/{id:long}/retry", (HttpContext ctx, ItamStore store, long id) => Guard(ctx, async u => { await store.RetryMail(u, id, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/mail/run-reminders", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => Results.Ok(new { queued = await store.RunMailJobsNow(u, ctx.RequestAborted) })));
        api.MapGet("/reports", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => Results.Ok(await store.Reports(u, ctx.RequestAborted))));
        api.MapGet("/reports/{key}", (HttpContext ctx, ItamStore store, string key, DateOnly? from, DateOnly? to, string? region) => Guard(ctx, async u => Results.Ok(await store.Report(u, key, from, to, region, ctx.RequestAborted))));
        api.MapGet("/reports/{key}/excel", (HttpContext ctx, ItamStore store, string key, DateOnly? from, DateOnly? to, string? region) => Guard(ctx, async u => { var f = await store.ReportExcel(u, key, from, to, region, ctx.RequestAborted); return Results.File(f.Content, f.MediaType, f.FileName); }));
        api.MapGet("/alerts", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => Results.Ok(await store.Alerts(u, ctx.RequestAborted))));
        // End of life (step 5): loss cases, disposals, sales, leavers, AD computer comparison, licenses.
        api.MapGet("/loss", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => Results.Ok(await store.LossCases(u, ctx.RequestAborted))));
        api.MapGet("/loss/{id:int}", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => Results.Ok(await store.LossCase(u, id, ctx.RequestAborted))));
        api.MapPost("/loss", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => { var (d, f) = await Form<ItamLossInput>(ctx.Request); return Results.Ok(await store.ReportLoss(u, d, f, ctx.RequestAborted)); }));
        api.MapPost("/loss/{id:int}/assign", (HttpContext ctx, ItamStore store, int id, ItamLossAssign input) => Guard(ctx, async u => { await store.AssignLoss(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/loss/{id:int}/security", (HttpContext ctx, ItamStore store, int id, ItamLossText input) => Guard(ctx, async u => { await store.LossSecurity(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/loss/{id:int}/findings", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => { var (d, f) = await Form<ItamLossFindings>(ctx.Request); await store.LossFindings(u, id, d, f, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/loss/{id:int}/decision", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => { var (d, f) = await Form<ItamLossDecision>(ctx.Request); await store.DecideLoss(u, id, d, f, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/loss/{id:int}/settlement", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => { var (d, f) = await Form<ItamLossText>(ctx.Request); await store.SettleLoss(u, id, d, f, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/loss/{id:int}/writeoff", (HttpContext ctx, ItamStore store, int id, ItamLossText input) => Guard(ctx, async u => { await store.RequestWriteOff(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/loss/{id:int}/writeoff/decision", (HttpContext ctx, ItamStore store, int id, ItamDecision input) => Guard(ctx, async u => { await store.DecideWriteOff(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/loss/{id:int}/recovery", (HttpContext ctx, ItamStore store, int id, ItamLossRecovery input) => Guard(ctx, async u => { await store.RecoverLoss(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/loss/{id:int}/review", (HttpContext ctx, ItamStore store, int id, ItamLossText input) => Guard(ctx, async u => { await store.ReviewRecovery(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapGet("/loss/{id:int}/file/{which}", (HttpContext ctx, ItamStore store, int id, string which) => Guard(ctx, async u => { var f = await store.LossFile(u, id, which, ctx.RequestAborted); return Results.File(f.Content, f.MediaType, f.FileName); }));
        api.MapGet("/disposals", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => Results.Ok(await store.Disposals(u, ctx.RequestAborted))));
        api.MapPost("/disposals", (HttpContext ctx, ItamStore store, ItamDisposalInput input) => Guard(ctx, async u => Results.Ok(await store.RequestDisposal(u, input, ctx.RequestAborted))));
        api.MapPost("/disposals/{id:int}/decision", (HttpContext ctx, ItamStore store, int id, ItamDecision input) => Guard(ctx, async u => { await store.DecideDisposal(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/disposals/{id:int}/complete", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => { var (d, f) = await Form<ItamDisposalCompletion>(ctx.Request); await store.CompleteDisposal(u, id, d, f, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/disposals/{id:int}/cancel", (HttpContext ctx, ItamStore store, int id, ItamCancelInput input) => Guard(ctx, async u => { await store.CancelDisposal(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapGet("/disposals/{id:int}/evidence", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => { var f = await store.DisposalEvidence(u, id, ctx.RequestAborted); return Results.File(f.Content, f.MediaType, f.FileName); }));
        api.MapGet("/sales", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => Results.Ok(await store.Sales(u, ctx.RequestAborted))));
        api.MapPost("/sales", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => { var (d, f) = await Form<ItamSaleInput>(ctx.Request); return Results.Ok(await store.RecordSale(u, d, f, ctx.RequestAborted)); }));
        api.MapGet("/sales/{id:int}/proof", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => { var f = await store.SaleProof(u, id, ctx.RequestAborted); return Results.File(f.Content, f.MediaType, f.FileName); }));
        api.MapGet("/leavers", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => Results.Ok(await store.Leavers(u, ctx.RequestAborted))));
        api.MapPost("/leavers", (HttpContext ctx, ItamStore store, ItamResignInput input) => Guard(ctx, async u => Results.Ok(await store.MarkResigned(u, input, ctx.RequestAborted))));
        api.MapPost("/leavers/{id:int}/complete", (HttpContext ctx, ItamStore store, int id, ItamLossText input) => Guard(ctx, async u => { await store.CompleteClearance(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/leavers/{id:int}/cancel", (HttpContext ctx, ItamStore store, int id, ItamCancelInput input) => Guard(ctx, async u => { await store.CancelClearance(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/leavers/{id:int}/disabled", (HttpContext ctx, ItamStore store, int id, ItamLossText input) => Guard(ctx, async u => { await store.UserDisabled(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapGet("/ad-compare", (HttpContext ctx, ItamStore store, int? run) => Guard(ctx, async u => Results.Ok(await store.AdComparison(u, run, ctx.RequestAborted))));
        api.MapPost("/ad-compare/run", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => Results.Ok(await store.RunAdComparison(u,
            ctx.RequestServices.GetService<IItamComputerDirectory>() ?? throw new ArgumentException("Active Directory is not connected yet (it comes with the production set-up)."), ctx.RequestAborted))));
        api.MapPost("/ad-compare/review", (HttpContext ctx, ItamStore store, ItamAdReview input) => Guard(ctx, async u => { await store.ReviewAdLine(u, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/ad-compare/unreview", (HttpContext ctx, ItamStore store, ItamAdReview input) => Guard(ctx, async u => { await store.UnreviewAdLine(u, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapGet("/licenses", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => Results.Ok(await store.Licenses(u, ctx.RequestAborted))));
        api.MapGet("/movements/find", (HttpContext ctx, ItamStore store, string? no) => Guard(ctx, async u => Results.Ok(await store.MovementByNo(u, no, ctx.RequestAborted))));
        api.MapGet("/movements/assets", (HttpContext ctx, ItamStore store, string? q, int? warehouse) => Guard(ctx, async u => Results.Ok(await store.MovementAssets(u, q, warehouse, ctx.RequestAborted))));
        api.MapGet("/movements/{id:int}", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => Results.Ok(await store.Movement(u, id, ctx.RequestAborted))));
        api.MapPost("/movements", (HttpContext ctx, ItamStore store, ItamMovementInput input) => Guard(ctx, async u => Results.Ok(await store.CreateMovement(u, input, ctx.RequestAborted))));
        api.MapPost("/movements/{id:int}/decision", (HttpContext ctx, ItamStore store, int id, ItamMovementDecision input) => Guard(ctx, async u => { await store.DecideMovement(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/movements/{id:int}/dispatch", (HttpContext ctx, ItamStore store, int id, ItamMovementLines input) => Guard(ctx, async u => { await store.DispatchMovement(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/movements/{id:int}/receipt", (HttpContext ctx, ItamStore store, int id, ItamMovementReceipt input) => Guard(ctx, async u => { await store.ReceiveMovement(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/movements/{id:int}/resolve", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => { var (d, f) = await Form<ItamMovementResolve>(ctx.Request); await store.ResolveMovement(u, id, d, f, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/movements/{id:int}/return", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => { await store.ReturnMovement(u, id, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/movements/{id:int}/cancel", (HttpContext ctx, ItamStore store, int id, ItamCancelInput input) => Guard(ctx, async u => { await store.CancelMovement(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapGet("/movements/{id:int}/pass.pdf", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => Pdf(await store.GatePassPdf(u, id, ctx.RequestAborted))));
        api.MapGet("/movements/{id:int}/evidence/{line:int}", (HttpContext ctx, ItamStore store, int id, int line) => Guard(ctx, async u => { var f = await store.MovementEvidence(u, id, line, ctx.RequestAborted); return Results.File(f.Content, f.MediaType, f.FileName); }));
        api.MapGet("/maintenance", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => Results.Ok(await store.MaintenanceOrders(u, ctx.RequestAborted))));
        api.MapGet("/maintenance/asset", (HttpContext ctx, ItamStore store, string? no) => Guard(ctx, async u => Results.Ok(await store.MaintenanceAsset(u, no, ctx.RequestAborted))));
        api.MapPost("/maintenance", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => { var (d, f) = await Form<ItamMaintenanceOrderInput>(ctx.Request); return Results.Ok(await store.CreateMaintenanceOrder(u, d, f, ctx.RequestAborted)); }));
        api.MapPost("/maintenance/{id:int}/decision", (HttpContext ctx, ItamStore store, int id, ItamDecision input) => Guard(ctx, async u => { await store.DecideMaintenance(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/maintenance/{id:int}/dispatch", (HttpContext ctx, ItamStore store, int id, ItamMaintenanceDispatch input) => Guard(ctx, async u => { await store.DispatchMaintenance(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/maintenance/{id:int}/return", (HttpContext ctx, ItamStore store, int id, ItamMaintenanceReturn input) => Guard(ctx, async u => { await store.ReturnMaintenance(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/maintenance/{id:int}/cancel", (HttpContext ctx, ItamStore store, int id, ItamCancelInput input) => Guard(ctx, async u => { await store.CancelMaintenance(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/maintenance/{id:int}/expected", (HttpContext ctx, ItamStore store, int id, ItamMaintenanceExpected input) => Guard(ctx, async u => { await store.ExpectedMaintenance(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapGet("/maintenance/{id:int}/quotation", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => { var f = await store.MaintenanceQuotation(u, id, ctx.RequestAborted); return Results.File(f.Content, f.MediaType, f.FileName); }));
        api.MapPost("/assets/{id:int}/it-repair", (HttpContext ctx, ItamStore store, int id, ItamItRepair input) => Guard(ctx, async u => { await store.ItRepair(u, id, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
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
        api.MapGet("/deliveries/{id:int}/return-places", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => Results.Ok(await store.ReturnPlaces(u, id, ctx.RequestAborted))));
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
        api.MapGet("/admin/ad-sync", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => Results.Ok(await store.DirectorySyncStatus(u, ctx.RequestServices.GetService<ItamDirectorySync>(), ctx.RequestAborted))));
        api.MapPost("/admin/ad-sync", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => Results.Ok(await store.RunDirectorySync(u, ctx.RequestServices.GetService<ItamDirectorySync>(), ctx.RequestAborted))));
        api.MapGet("/admin/csv/{list}", (HttpContext ctx, ItamStore store, string list) => Guard(ctx, async u => { var f = await store.ExportCsv(u, list, ctx.RequestAborted); return Results.File(f.Content, f.MediaType, f.FileName); }));
        api.MapPost("/admin/csv/{list}", (HttpContext ctx, ItamStore store, string list) => Guard(ctx, async u => { var (d, f) = await Form<ItamCsvImport>(ctx.Request); return Results.Ok(await store.ImportCsv(u, list, d, f, ctx.RequestAborted)); }));
        api.MapGet("/backups", (HttpContext ctx, ItamStore store) => Guard(ctx, async u => Results.Ok(await store.Backups(u, ctx.RequestAborted))));
        api.MapPost("/backups", (HttpContext ctx, ItamStore store, ItamBackupInput input) => Guard(ctx, async u => { await store.SetBackup(u, input, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));
        api.MapPost("/backups/{id:int}/cancel", (HttpContext ctx, ItamStore store, int id) => Guard(ctx, async u => { await store.CancelBackup(u, id, ctx.RequestAborted); return Results.Ok(new { ok = true }); }));

        // The QR code on the asset label: opens the asset list filtered on the Asset No.
        app.MapGet("/a/{no}", (string no) => Results.Redirect("/?q=" + Uri.EscapeDataString(no)));
        app.MapGet("/r/{no}", (string no) => Results.Redirect("/request.html?no=" + Uri.EscapeDataString(no)));
        app.MapGet("/g/{no}", (string no) => Results.Redirect("/movement.html?no=" + Uri.EscapeDataString(no)));
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
