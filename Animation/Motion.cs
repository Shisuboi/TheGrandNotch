namespace TheGrandNotch.Animation;

/// <summary>
/// Système de mouvement unifié. Toutes les interactions puisent dans ce petit jeu
/// de ressorts nommés + durées de référence → une seule « physique » cohérente
/// dans toute l'app, à la manière des springs réutilisés d'iOS.
///
/// Remplace les ~9 amplitudes BackEase et ~16 durées dispersées d'avant.
/// </summary>
public static class Motion
{
    // ── Durées de référence (ms) — 3 paliers au lieu d'une douzaine de valeurs ──
    public const double DurationFast   = 220;  // micro-interactions (press, morph)
    public const double DurationMedium = 360;  // standard (HUD, pops)
    public const double DurationSlow   = 460;  // structurel (resize de l'île)

    // ── Ressorts nommés (instances figées, partageables sans risque) ────────────

    /// <summary>Vif, léger dépassement (~5 %). Apparitions, ouverture, resize.</summary>
    public static SpringEase Snappy { get; } = Frozen(0.70, 10.0);

    /// <summary>Joueur, dépassement marqué (~20 %). Rebonds de boutons, pops d'icônes.</summary>
    public static SpringEase Bouncy { get; } = Frozen(0.45, 12.0);

    /// <summary>Amorti critique, AUCUN dépassement. Fermetures, disparitions.</summary>
    public static SpringEase Smooth { get; } = Frozen(1.00, 8.0);

    /// <summary>Ressort sur mesure (non figé) — pour les cas pilotés par un réglage.</summary>
    public static SpringEase Spring(double damping, double stiffness = 10.0)
        => new() { Damping = damping, Stiffness = stiffness };

    private static SpringEase Frozen(double damping, double stiffness)
    {
        var s = new SpringEase { Damping = damping, Stiffness = stiffness };
        s.Freeze();
        return s;
    }
}
