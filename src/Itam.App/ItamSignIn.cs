using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Itam.App;

/// <summary>Checks an AD username and password (production: a bind to the domain controller over LDAPS). IT Assets never
/// stores or logs the password. True = right password; false = wrong username or password; an exception = AD unavailable.</summary>
public interface IItamPasswordCheck { Task<bool> Check(string account, string password, CancellationToken token); }

public sealed record ItamSignInInput(string? Account, string? Password, string? Return = null);

/// <summary>Repeated wrong passwords are refused here before AD is asked, so a guessing attempt cannot lock the person's AD
/// account: 5 failures for one account, or 20 from one address, within 15 minutes wait 15 minutes.</summary>
public sealed class ItamSignInThrottle(TimeProvider? time = null)
{
    public const int AccountLimit = 5, AddressLimit = 20;
    static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    readonly ConcurrentDictionary<string, List<DateTimeOffset>> failures = new(StringComparer.OrdinalIgnoreCase);
    readonly TimeProvider clock = time ?? TimeProvider.System;

    int Recent(string key)
    {
        if (!failures.TryGetValue(key, out var list)) return 0;
        lock (list) { list.RemoveAll(t => clock.GetUtcNow() - t > Window); return list.Count; }
    }

    public bool Blocked(string account, string address) => Recent("a:" + account) >= AccountLimit || Recent("ip:" + address) >= AddressLimit;

    public void Failed(string account, string address)
    {
        foreach (var key in new[] { "a:" + account, "ip:" + address })
        {
            var list = failures.GetOrAdd(key, _ => []);
            lock (list) list.Add(clock.GetUtcNow());
        }
    }

    public void Succeeded(string account) => failures.TryRemove("a:" + account, out _);
}

/// <summary>The sign-in page (owner, 22 October 2026): the person types their AD username and password; IT Assets checks
/// them with AD and keeps them signed in with a site cookie (8 hours, renewed while they work, ended by Sign out or closing
/// the browser). Only an enabled employee in the AD copy is let in.</summary>
public static class ItamSignIn
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    public static IServiceCollection AddItamSignIn(this IServiceCollection services, bool httpsOnly = true)
    {
        services.AddSingleton<ItamSignInThrottle>();
        services.AddAuthentication(Scheme).AddCookie(Scheme, o =>
        {
            o.Cookie.Name = "itam.signin"; o.Cookie.HttpOnly = true; o.Cookie.SecurePolicy = httpsOnly ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            // Lax: links in emails (one-click decisions) open the site signed in.
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.ExpireTimeSpan = TimeSpan.FromHours(8); o.SlidingExpiration = true;
            // The pages and the API answer 401 themselves; the page script opens the sign-in page.
            o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        return services;
    }

    /// <summary>username, DOMAIN\username or username@domain to the account name; null when it cannot be one.</summary>
    public static string? Account(string? typed) => string.IsNullOrWhiteSpace(typed) ? null : ItamWindowsSession.Account(typed.Trim());

    /// <summary>Where to go after signing in: a page of this site only.</summary>
    public static string ReturnTo(string? path) =>
        path is { Length: > 1 and <= 500 } p && p[0] == '/' && p[1] != '/' && p[1] != '\\' && !p.Any(char.IsControl) && !p.StartsWith("/signin", StringComparison.OrdinalIgnoreCase) ? p : "/";

    public static void MapItamSignIn(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/signin", (HttpContext ctx, IAntiforgery csrf) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { csrf = csrf.GetAndStoreTokens(ctx).RequestToken });
        });
        app.MapPost("/api/signin", async (HttpContext ctx, ItamSignInInput input, IAntiforgery csrf, IItamPasswordCheck check, ItamStore store, ItamSignInThrottle throttle, ILoggerFactory logs) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var log = logs.CreateLogger("IT Assets sign-in");
            try { await csrf.ValidateRequestAsync(ctx); }
            catch (AntiforgeryValidationException) { return Results.Json(new { error = "Reload the page and try again." }, statusCode: 400); }
            var account = Account(input.Account);
            if (account is null || string.IsNullOrEmpty(input.Password)) return Results.Json(new { error = "Type your username and password." }, statusCode: 400);
            if (input.Password.Length > 256) return Results.Json(new { error = "The username or password is wrong." }, statusCode: 401);
            var address = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (throttle.Blocked(account, address))
            {
                log.LogWarning("Sign-in refused for {Account} from {Address}: too many wrong passwords.", account, address);
                return Results.Json(new { error = "Too many wrong passwords. Wait 15 minutes and try again, or ask IT." }, statusCode: 429);
            }
            bool right;
            try { right = await check.Check(account, input.Password, ctx.RequestAborted); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Sign-in for {Account}: Active Directory did not answer.", account);
                return Results.Json(new { error = "Sign-in is not available now (Active Directory did not answer). Try again in a few minutes." }, statusCode: 503);
            }
            if (!right)
            {
                throttle.Failed(account, address);
                log.LogWarning("Sign-in failed for {Account} from {Address}: wrong username or password.", account, address);
                return Results.Json(new { error = "The username or password is wrong." }, statusCode: 401);
            }
            if (await store.EmployeeByAccount(account, ctx.RequestAborted) is null)
            {
                log.LogWarning("Sign-in for {Account}: right password but not an enabled employee in the AD copy.", account);
                return Results.Json(new { error = "Your account is not in IT Assets yet (accounts are copied from AD every hour). Ask IT." }, statusCode: 403);
            }
            throttle.Succeeded(account);
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, account)], Scheme);
            await ctx.SignInAsync(Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false });
            log.LogInformation("Signed in: {Account} from {Address}.", account, address);
            return Results.Ok(new { go = ReturnTo(input.Return) });
        });
        app.MapPost("/api/signout", async (HttpContext ctx, IAntiforgery csrf) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            try { await csrf.ValidateRequestAsync(ctx); }
            catch (AntiforgeryValidationException) { return Results.Json(new { error = "Reload the page and try again." }, statusCode: 400); }
            await ctx.SignOutAsync(Scheme);
            return Results.Ok(new { ok = true });
        });
    }
}
