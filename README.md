# Jellyfin Plugin Torr2Jelly 🚀🍿

<p align="center">
  <img src="logo.png" alt="Torr2Jelly Plugin Logo" width="128" height="128" />
</p>

<p align="center">
  <strong>Standalone Torrent Streaming & Prowlarr/Torznab Hub for Jellyfin</strong>
</p>

---

## 🌟 Overview

**Torr2Jelly** is a native .NET 8 plugin for **Jellyfin** that brings seamless on-the-fly torrent streaming into your media server without pre-downloading gigabytes of files to disk.

It connects to your **TorrServer** instance, provides an integrated global tracker search via **Prowlarr** / **Torznab** directly inside the Jellyfin web client, and automatically organizes media into structured `.strm` libraries with full TMDb/IMDb metadata support and season indexing.

Fully compatible with **Jellyfin Web**, **Android TV**, **Apple TV**, **Tizen (Samsung)**, **webOS (LG)**, and iOS / Android mobile apps.

---

## ✨ Key Features

- ⚡ **Instant On-The-Fly Torrent Streaming**: Video playback starts within seconds via TorrServer and generated `.strm` links, featuring full HTTP range request seeking, audio track switching, and subtitle selection.
- 💾 **High-Speed Disk Buffering (SSD/HDD Cache)**: Configurable pre-allocated disk cache (e.g., 10–100 GB) instead of memory-constrained RAM cache, preventing server OOM crashes on high-bitrate 4K REMUX releases.
- 🔍 **Integrated Global Jellyfin Search**: Search for movies and TV series directly through the Jellyfin search bar — if content is not yet in your library, find releases across your Prowlarr indexers and add them with one click.
- 🏷️ **Bonus & Tag Filtering (Freeleech / Gold)**: Support for Freeleech, Gold, Silver, Bronze, and author tags to protect your private tracker ratio, with admin-only enforcement options.
- 📁 **Smart TV & Movie Organization**: Intelligent episode and season parsing (`Season 01/Show - S01E01.strm`), multi-file torrent handling, and automated NFO generation linked to TMDb and IMDb.
- 🎨 **Modern Dark UI (Glassmorphism)**: Built-in configuration dashboard with real-time connectivity diagnostics for TorrServer and Prowlarr, magnet link importer, tracker tag manager, and bilingual English / Ukrainian support.
- 🛡️ **Durable Settings Protection**: Automatic configuration backup and restore preserving your setup across plugin updates.

---

## 📦 Installation via Jellyfin Plugin Repository

1. Open your Jellyfin Dashboard (**Dashboard** ➔ **Plugins**).
2. Switch to the **Repositories** tab and click **+** (Add Repository):
   - **Repository Name**: `Torr2Jelly Repository`
   - **Repository URL**:
     ```text
     https://raw.githubusercontent.com/D1st3f-PRJCTS/jellyfin-plugin-torr2jelly/main/manifest.json
     ```
3. Switch to the **Catalog** tab, find **Torr2Jelly**, and click **Install**.
4. Restart your Jellyfin server.
5. Navigate to **Torr2Jelly** in the server sidebar under the Plugins section to configure your endpoints.

---

## ⚙️ Configuration

In the **Torr2Jelly** settings tab:

1. **TorrServer URL**: The address of your TorrServer instance (e.g., `http://torrserver:8090`).
2. **Prowlarr URL & API Key**: Your Prowlarr host (e.g., `http://prowlarr:9696`) and API key.
3. **Library Paths**: Select your target Movies and TV Shows library folders where `.strm` files will be placed.
4. **Cache & Diagnostics**: Use the built-in **Test Connection** buttons to verify communication with both TorrServer and Prowlarr.

---

## 🛠️ Build from Source

```bash
# Clone the repository
git clone https://github.com/D1st3f-PRJCTS/jellyfin-plugin-torr2jelly.git
cd jellyfin-plugin-torr2jelly

# Restore dependencies
dotnet restore

# Build release
dotnet build -c Release
```

Copy the compiled assembly from `bin/Release/net8.0/Jellyfin.Plugin.Torr2Jelly.dll` into your Jellyfin plugins directory:
```bash
mkdir -p /path/to/jellyfin/config/plugins/Torr2Jelly
cp bin/Release/net8.0/Jellyfin.Plugin.Torr2Jelly.dll /path/to/jellyfin/config/plugins/Torr2Jelly/
```

---

## 📄 License

Distributed under the **MIT License**.
