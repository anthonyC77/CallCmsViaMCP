using Microsoft.AspNetCore.SignalR;

namespace SiteGuardian.Api.Hubs;

/// <summary>
/// Diffuse la progression des audits et corrections en temps réel vers le front.
/// Squelette Phase 0 — les messages de progression sont émis en Phase 1/3.
/// </summary>
public class ProgressHub : Hub
{
}
