# Claude Trading Bot – ASP.NET (.NET 8)

Ein KI-gestützter Trading-Bot mit ASP.NET Web-Dashboard, der Claude als Analyse-Engine nutzt
und über Interactive Brokers (IB Gateway/TWS) Aktien und ETFs handelt.

## Architektur

```
┌─────────────────────────────────────────────────┐
│              ASP.NET Web Dashboard               │
│   (Portfolio, Trades, Logs, Einstellungen)       │
├─────────────────────────────────────────────────┤
│            TradingEngine (Background)            │
│  ┌──────────┐  ┌──────────┐  ┌───────────────┐  │
│  │ Claude   │  │ IB       │  │ Risk          │  │
│  │ API      │◄─┤ Gateway  │◄─┤ Manager       │  │
│  │ Service  │  │ Service  │  │ (Stop-Loss)   │  │
│  └──────────┘  └──────────┘  └───────────────┘  │
├─────────────────────────────────────────────────┤
│         SQLite (Trades, Portfolio, Logs)          │
└─────────────────────────────────────────────────┘
```

## Setup

1. .NET 8 SDK installieren
2. IB Gateway oder TWS auf dem VPS installieren und API aktivieren
3. `appsettings.json` konfigurieren (API-Keys, IB-Verbindung)
4. `dotnet run` starten

## Konfiguration

Alle Einstellungen in `appsettings.json`:
- **Anthropic:ApiKey** – Dein Claude API-Key
- **InteractiveBrokers:Host/Port** – IB Gateway Verbindung
- **RiskManagement** – Stop-Loss, Max-Verlust, Position-Limits

## Sicherheitshinweis

⚠️ Immer zuerst mit Paper Trading testen!
⚠️ Dieser Bot ist ein Ausgangspunkt – kein fertiges Handelssystem.
⚠️ Automatisiertes Trading birgt erhebliche finanzielle Risiken.
