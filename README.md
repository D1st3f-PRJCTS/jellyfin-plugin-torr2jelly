# Jellyfin Plugin Torr2Jelly 🚀🍿

<p align="center">
  <img src="logo.png" alt="Torr2Jelly Plugin Logo" width="128" height="128" />
</p>

<p align="center">
  <strong>Standalone Torrent Streaming & Prowlarr/Torznab Hub for Jellyfin</strong>
</p>

---

## 🌟 Overview

**Torr2Jelly Plugin** is a native Jellyfin plugin (.NET 8) that brings full torrent streaming directly into your Jellyfin server.
It operates as a **complete standalone solution**: upon installation, the plugin automatically downloads and manages an embedded TorrServer-Matrix daemon, provides unified Prowlarr & Torznab search inside the Jellyfin Web UI, and generates `.strm` files on-demand for movies and TV series with automatic season structuring.

Works seamlessly on **Jellyfin Web**, **Android TV**, **Apple TV**, **Tizen**, and **iOS/Android** mobile clients.

---

## ✨ Key Features

- ⚡ **100% Standalone (Zero-Config)**: Embedded TorrServer process supervisor automatically downloads the binary for your platform (Linux x64/arm64, Windows, macOS) and manages its lifecycle.
- 📡 **Prowlarr & Torznab Integration**: Fast async tracker search with category filtering, seeders sorting, and VIP/Gold/Freeleech prioritization.
- 🍿 **Instant Streaming via `.strm`**: Generates clean `.strm` links directly into your Jellyfin media libraries. Video playback begins in seconds with full seeking (range requests) and audio/subtitle selection.
- 🗂️ **Smart Season Auto-Organizer**: Automatically parses `S01E02`, `1x05`, and multi-episode formats into clean `Season XX` folder structures.
- 🎨 **Native Glassmorphic UI**: Integrates into the Jellyfin Web sidebar with dark theme styling, remote D-pad navigation for Smart TVs, and Ukrainian/English localization.
- 🔄 **Dual-Mode Support**: Use the built-in embedded daemon or connect to an existing remote TorrServer instance.

---

## 📦 Installation via Jellyfin Plugin Repository

1. Open your **Jellyfin Admin Dashboard**.
2. Navigate to **Plugins** ➔ **Repositories**.
3. Click **+** (Add Repository) and enter:
   - **Repository Name**: `Torr2Jelly Repository`
   - **Repository URL**: `https://raw.githubusercontent.com/D1st3f-PRJCTS/jellyfin-plugin-torr2jelly/main/manifest.json`
4. Go to the **Catalog** tab, find **Torr2Jelly**, and click **Install**.
5. Restart your Jellyfin server.
6. Open the **Torr2Jelly** section from the sidebar to start streaming!

---

## 🛠️ Build from Source

```bash
# Clone the repository
git clone https://github.com/D1st3f-PRJCTS/jellyfin-plugin-torr2jelly.git
cd jellyfin-plugin-torr2jelly

# Restore dependencies
dotnet restore

# Build release DLL
dotnet build -c Release
```

The resulting `Jellyfin.Plugin.Torr2Jelly.dll` can be placed into your Jellyfin `plugins/Torr2Jelly/` directory.

---

## 📄 License

Distributed under the **MIT License**.
