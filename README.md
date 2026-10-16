# The Purge

**Author:** ThornFox · **Version:** 1.2.1 · **Framework:** Oxide/uMod for Rust

## English

Announces and manages a purge event: banner UI, info panel, optional timer, Notify support, configurable start/end commands and a clean RealPVE integration.

### Commands
| Command | Description |
|---|---|
| `/pbegin [minutes]` | Start the purge (without minutes it runs until `/pend`) |
| `/pend` | End the purge |
| `/purge` | Show the info panel |
| `purge.begin [minutes]` | Console/RCON: start the purge |
| `purge.end` | Console/RCON: end the purge |

### Permissions
- `thepurge.begin` – start the purge
- `thepurge.end` – end the purge
- `thepurge.info` – open the info panel

Example: `oxide.grant group admin thepurge.begin`

### RealPveMode
- `Unload` (default): RealPVE is unloaded for the purge, `server.pve` is set to `false` and restored afterwards. All rules are off, including raiding.
- `Hooks`: RealPVE stays loaded. All players (optionally sleepers) are switched to PvP via the official RealPVE API (`OnPlayerEnterPVP` / `OnPlayerExitPVP`). Corpses and backpacks become lootable. Building protection stays active (no raiding).
- `Off`: RealPVE is not touched.

The purge state is saved in `oxide/data/ThePurge.json`. It survives restarts and plugin reloads; the timer continues. A map wipe ends the purge.

Chat commands from the config are executed as the player who started/ended the purge. For timer or console runs use console commands.

### Changelog
- **1.2.1** – Permissions renamed from `purge.*` to `thepurge.*`. Existing grants are moved automatically on the first start.
- **1.2.0** – RealPVE integration reworked (unload mode with `server.pve` handling, hooks mode via the RealPVE PvP API), saved purge state, console commands, lang files.

### Tested on RustRentner
All ThornFox plugins are tested extensively on [rustrentner.de](https://rustrentner.de).

### License
MIT – see [LICENSE](LICENSE).

---

## Deutsch

Kündigt ein Purge-Event an und steuert es: Banner, Info-Panel, optionaler Timer, Notify, konfigurierbare Start-/End-Befehle und eine saubere RealPVE-Anbindung.

### Befehle
| Befehl | Beschreibung |
|---|---|
| `/pbegin [Minuten]` | Purge starten (ohne Minuten läuft er bis `/pend`) |
| `/pend` | Purge beenden |
| `/purge` | Info-Panel anzeigen |
| `purge.begin [Minuten]` | Konsole/RCON: Purge starten |
| `purge.end` | Konsole/RCON: Purge beenden |

### Berechtigungen
- `thepurge.begin` – Purge starten
- `thepurge.end` – Purge beenden
- `thepurge.info` – Info-Panel öffnen

Beispiel: `oxide.grant group admin thepurge.begin`

### RealPveMode
- `Unload` (Standard): RealPVE wird für den Purge entladen, `server.pve` auf `false` gesetzt und danach wiederhergestellt. Alle Regeln aus, inklusive Raiden.
- `Hooks`: RealPVE bleibt geladen. Alle Spieler (optional auch Schlafende) werden über die offizielle RealPVE-API auf PvP geschaltet. Leichen und Rucksäcke sind lootbar. Basisschutz bleibt aktiv (kein Raiden).
- `Off`: RealPVE wird nicht angefasst.

Der Purge-Zustand liegt in `oxide/data/ThePurge.json`. Er übersteht Neustarts und Plugin-Reloads, der Timer läuft weiter. Ein Map-Wipe beendet den Purge.

Chat-Befehle aus der Config werden als der Spieler ausgeführt, der den Purge gestartet/beendet hat. Für Timer- oder Konsolen-Läufe Konsolenbefehle verwenden.

### Änderungen
- **1.2.1** – Rechte von `purge.*` auf `thepurge.*` umbenannt. Bestehende Vergaben werden beim ersten Start automatisch übernommen.
- **1.2.0** – RealPVE-Anbindung überarbeitet (Entlade-Modus mit `server.pve`-Behandlung, Hooks-Modus über die RealPVE-PvP-API), gespeicherter Purge-Zustand, Konsolenbefehle, Lang-Dateien.

### Getestet auf RustRentner
Alle Plugins von ThornFox werden ausgiebig auf [rustrentner.de](https://rustrentner.de) getestet.

### Lizenz
MIT – siehe [LICENSE](LICENSE).
