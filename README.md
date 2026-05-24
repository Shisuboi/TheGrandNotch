# TheGrandNotch

Un overlay Dynamic Island pour Windows 11 — contrôle média, volume, calendrier et transfert de fichiers directement depuis le haut de votre écran.

Inspiré de [TheBoringNotch](https://github.com/TheBoringNotch/TheBoringNotch) (macOS).

[![Licence MIT](https://img.shields.io/badge/licence-MIT-blue.svg)](LICENSE)
[![Windows 11](https://img.shields.io/badge/Windows-10%2F11-0078D4?logo=windows)](https://github.com/Shisuboi/TheGrandNotch/releases/latest)

---

## Fonctionnalités

| Fonctionnalité | Description |
|---------------|-------------|
| **Contrôle média** | Lecture/pause, piste suivante/précédente, pochette d'album — compatible avec toutes les apps Windows Media (Spotify, navigateurs, etc.) |
| **HUD volume** | Affichage du niveau sonore en temps réel à chaque variation du volume système |
| **Calendrier** | Lecture d'un calendrier au format iCal (Google Calendar, Outlook, etc.) via une URL `.ics` |
| **LocalSend** | Transfert de fichiers en P2P sur le réseau local (LAN) — compatible iOS, Android, macOS, Linux |
| **Icône tray** | Accès rapide aux réglages et fermeture propre depuis la zone de notification |
| **Démarrage automatique** | Se lance avec Windows (configurable via les paramètres) |

---

## Prérequis

- **Windows 10** (build 19041 / 20H1) ou **Windows 11** — toutes versions
- Architecture **64 bits** (x64)
- Aucune installation de .NET requise (embarqué dans l'exe)

---

## Installation

1. Téléchargez la dernière version dans les [Releases](https://github.com/Shisuboi/TheGrandNotch/releases/latest) et décompressez l'archive.
2. Double-cliquez sur **`install.bat`** → acceptez l'invite UAC.

C'est tout. L'installateur :
- Signe l'exécutable (requis par Windows pour rester au-dessus de toutes les fenêtres)
- Copie l'application dans `C:\Program Files\TheGrandNotch\`
- Crée un raccourci sur le **bureau** et dans le **menu Démarrer**
- Lance TheGrandNotch automatiquement

> Aucune donnée n'est envoyée : le certificat de signature est créé localement sur votre machine.

### Mises à jour

Double-cliquez à nouveau sur `install.bat` depuis la nouvelle version. Vos réglages sont conservés.

---

## Utilisation

| Geste | Action |
|-------|--------|
| **Survol** de la notch | Expansion avec les contrôles média |
| **Clic gauche** sur la piste | Lecture / pause |
| **Clic droit** sur la notch | Menu contextuel (paramètres, fermer) |

Les réglages (taille, animations, démarrage auto, URL iCal) sont accessibles via l'icône dans la barre des tâches ou le clic droit sur la notch.

### Calendrier iCal

1. Clic droit → *Paramètres* → champ URL calendrier
2. Entrez l'URL de votre calendrier `.ics` (Google Calendar, Outlook, Nextcloud…)
3. Les événements s'affichent directement dans la notch

### LocalSend (transfert de fichiers)

TheGrandNotch intègre le protocole [LocalSend](https://localsend.org) pour envoyer et recevoir des fichiers sur le réseau local sans cloud. Les appareils compatibles sont détectés automatiquement.

---

## Confidentialité

TheGrandNotch ne collecte **aucune donnée**. Aucune télémétrie, aucun serveur externe, aucune connexion à Internet sauf :

- L'URL iCal que **vous** configurez (optionnel)
- La découverte LocalSend sur votre réseau local uniquement (UDP multicast, jamais Internet)

Toutes les données utilisateur restent sur votre machine (`%AppData%\TheGrandNotch\`).

---

## Fonctionnalités à venir

- Raccourci clavier global pour ouvrir la notch
- Support multi-moniteurs
- Notifications système dans la notch

Voir le [backlog complet](BACKLOG.md) pour les détails et priorités.

---

## Compilation depuis les sources

```powershell
# Pré-requis : .NET 8 SDK
git clone https://github.com/Shisuboi/TheGrandNotch.git
cd TheGrandNotch
dotnet build
```

Pour un build de distribution (self-contained win-x64) :
```powershell
dotnet publish /p:PublishProfile=Release-x64
# Sortie dans publish\
```

---

## Licence

À définir.
