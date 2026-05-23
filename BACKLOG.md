# TheGrandNotch — Feature Backlog

Features brainstormed but not yet implemented. Listed in rough priority order.

---

## 1. Charging / Battery Live Activity
**Quoi :** Quand on branche l'alimentation, le notch s'anime (mini pulse) et affiche l'icône batterie + pourcentage. En cas de batterie faible (< 20 %), alerte discrète dans le notch.  
**Critique :** Sur desktop la batterie n'existe pas — utile uniquement sur laptop. Lire `SystemParameters.PowerLineStatus` ou `ManagementObject Win32_Battery`. Assez simple à implémenter.  
**Effort :** Faible–Moyen

---

## 2. Keep-Awake (Caffeine) Toggle
**Quoi :** Un bouton dans le notch étendu (ou hotkey) pour empêcher Windows de mettre l'écran en veille. Une petite icône « café » apparaît en mode mini pendant que c'est actif.  
**Critique :** `SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED)` — trivial à appeler, mais l'indicateur mini demande une intégration dans `UpdateNotchVisual`.  
**Effort :** Faible

---

## 3. Global Hotkey pour expand/peek
**Quoi :** `Win+N` (ou configurable) expand le notch depuis n'importe quelle app. Utile pour voir les médias sans bouger la souris vers le haut de l'écran.  
**Critique :** `RegisterHotKey` / `UnregisterHotKey` via P/Invoke. Conflit potentiel avec les hotkeys système. Rendre la combinaison configurable dès le départ.  
**Effort :** Faible–Moyen

---

## 4. Audio Output Device Switcher
**Quoi :** Dans le notch étendu, liste les sorties audio disponibles (haut-parleurs, casque, HDMI…) et permet de basculer d'un clic.  
**Critique :** Nécessite `IMMDeviceEnumerator.EnumAudioEndpoints` + `IPolicyConfig` (non documenté, GUID privé). La partie enum est robuste ; le switch peut casser sur certaines versions de Windows.  
**Effort :** Moyen

---

## 5. Multi-session Media Switcher
**Quoi :** Quand plusieurs apps jouent de la musique (Spotify + navigateur), le notch affiche une liste pour choisir quelle session SMTC contrôler.  
**Critique :** `GlobalSystemMediaTransportControlsSessionManager.GetSessions()` déjà dans le projet. Surtout du travail UI : afficher la liste proprement dans l'espace limité du notch étendu.  
**Effort :** Moyen

---

## 6. Timer / Pomodoro Live Activity
**Quoi :** Lancer un chrono (25/5 min Pomodoro ou durée libre) depuis le notch étendu. En mode mini, une barre de progression circulaire ou linéaire indique le temps restant.  
**Critique :** La partie timer est triviale (`DispatcherTimer`). L'intégration visuelle en mini sans surcharger l'espace existant (mini déjà utilisé par volume + média) est le vrai défi.  
**Effort :** Moyen

---

## 7. Multi-Monitor Support
**Quoi :** Le notch se positionne sur l'écran principal configuré dans Windows (pas forcément `SystemParameters.PrimaryScreenWidth`). Option pour le déplacer vers un autre moniteur.  
**Critique :** Utiliser `Screen.AllScreens` / `WpfScreenHelper` et surveiller `SystemEvents.DisplaySettingsChanged`. Sans ça, le notch se retrouve au mauvais endroit sur les setups multi-écrans.  
**Effort :** Moyen

---

## 8. Screenshot-to-Shelf / Clipboard
**Quoi :** Raccourci pour capturer une région d'écran. La capture apparaît dans le notch (miniature) et est copiée dans le presse-papiers ou envoyée via LocalSend (déjà implémenté).  
**Critique :** Capture via `CopyFromScreen` ou l'API `IGraphicsCaptureItem` (Windows 10 1903+). Le vrai challenge : sélectionner la région sans que la fenêtre du notch interfère (la masquer pendant la capture).  
**Effort :** Élevé

---

## 9. Notifications Mirror (Windows UserNotificationListener)
**Quoi :** Afficher les notifications Windows dans le notch (à la Dynamic Island sur iOS). Tap pour expand et voir le contenu.  
**Critique :** `UserNotificationListener` nécessite une **capacité UWP déclarée** dans le manifeste — impossible dans une WPF classique sans packaging MSIX. C'est le plus risqué techniquement. À aborder en dernier.  
**Effort :** Très élevé (nécessite MSIX ou workaround COM non officiel)

---

## Ordre d'implémentation suggéré

| Priorité | Feature | Raison |
|----------|---------|--------|
| 1 | Keep-Awake Toggle | Effort minimal, impact immédiat |
| 2 | Global Hotkey | Simple + très utilisable au quotidien |
| 3 | Battery Live Activity | Complète les "live activities" (volume déjà fait) |
| 4 | Audio Output Switcher | Logique suite au Volume HUD |
| 5 | Multi-session Media | Base SMTC déjà là |
| 6 | Timer / Pomodoro | Demande réflexion UI mini |
| 7 | Multi-Monitor | Correctif important pour les setups multi-écrans |
| 8 | Screenshot-to-Shelf | Complexité capture + intégration LocalSend |
| 9 | Notifications Mirror | En dernier (risque MSIX) |
