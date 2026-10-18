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
It connects seamlessly to your TorrServer instance (e.g. Docker container or remote instance via VPN), provides unified Prowlarr & Torznab search directly inside the Jellyfin Web UI, and generates `.strm` files on-demand for movies and TV series with automatic season structuring.

Works seamlessly on **Jellyfin Web**, **Android TV**, **Apple TV**, **Tizen**, and **iOS/Android** mobile clients.

---

## ✨ Key Features

- ⚡ **Seamless TorrServer Integration**: Connects directly to TorrServer (e.g. `http://torrserver:8090`), managing connection health, stream URL routing, and RAM cache configuration.
- 📡 **Prowlarr & Torznab Integration**: Fast async tracker search with category filtering, seeders sorting, and VIP/Gold/Freeleech prioritization.
- 🍿 **Instant Streaming via `.strm`**: Generates clean `.strm` links directly into your Jellyfin media libraries. Video playback begins in seconds with full seeking (range requests) and audio/subtitle selection.
- 🗂️ **Smart Season Auto-Organizer**: Automatically parses `S01E02`, `1x05`, and multi-episode formats into clean `Season XX` folder structures.
- 🎨 **Native Glassmorphic UI**: Integrates into the Jellyfin Web sidebar with dark theme styling, remote D-pad navigation for Smart TVs, and Ukrainian/English localization.

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
