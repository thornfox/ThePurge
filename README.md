# The Purge 1.2.0 (ThornFox)

## English

Announces and manages a purge event: banner UI, info panel, optional timer, Notify support, configurable start/end commands and a clean RealPVE integration.

**Commands:** `/pbegin [minutes]`, `/pend`, `/purge` · Console/RCON: `purge.begin [minutes]`, `purge.end`
**Permissions:** `purge.begin`, `purge.end`, `purge.info`

**RealPveMode**
- `Unload` (default): RealPVE is unloaded for the purge, `server.pve` is set to `false` and restored afterwards. All rules are off, including raiding.
- `Hooks`: RealPVE stays loaded. All players (optionally sleepers) are switched to PvP via the official RealPVE API (`OnPlayerEnterPVP` / `OnPlayerExitPVP`). Corpses and backpacks become lootable. Building protection stays active (no raiding).
- `Off`: RealPVE is not touched.

The purge state is saved in `oxide/data/ThePurge.json`. It survives restarts and plugin reloads; the timer continues. A map wipe ends the purge.

Chat commands from the config are executed as the player who started/ended the purge. For timer or console runs use console commands.

## Deutsch

Kündigt ein Purge-Event an und steuert es: Banner, Info-Panel, optionaler Timer, Notify, konfigurierbare Start-/End-Befehle und eine saubere RealPVE-Anbindung.

**Befehle:** `/pbegin [Minuten]`, `/pend`, `/purge` · Konsole/RCON: `purge.begin [Minuten]`, `purge.end`
**Rechte:** `purge.begin`, `purge.end`, `purge.info`

**RealPveMode**
- `Unload` (Standard): RealPVE wird für den Purge entladen, `server.pve` auf `false` gesetzt und danach wiederhergestellt. Alle Regeln aus, inklusive Raiden.
- `Hooks`: RealPVE bleibt geladen. Alle Spieler (optional auch Schlafende) werden über die offizielle RealPVE-API auf PvP geschaltet. Leichen und Rucksäcke sind lootbar. Basisschutz bleibt aktiv (kein Raiden).
- `Off`: RealPVE wird nicht angefasst.

Der Purge-Zustand liegt in `oxide/data/ThePurge.json`. Er übersteht Neustarts und Plugin-Reloads, der Timer läuft weiter. Ein Map-Wipe beendet den Purge.

Chat-Befehle aus der Config werden als der Spieler ausgeführt, der den Purge gestartet/beendet hat. Für Timer- oder Konsolen-Läufe Konsolenbefehle verwenden.
