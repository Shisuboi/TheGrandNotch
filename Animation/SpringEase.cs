using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace TheGrandNotch.Animation;

/// <summary>
/// Easing fondée sur la physique d'un ressort amorti (oscillateur harmonique),
/// bien plus proche du « feel » iOS que <see cref="BackEase"/> (simple polynôme).
///
/// La courbe démarre à vitesse nulle (modèle SwiftUI <c>.spring</c>), accélère,
/// dépasse la cible selon l'amortissement, puis se stabilise — exactement la
/// réponse indicielle d'un système masse-ressort.
///
/// f(t) = 1 − e^(−ζωt) · ( cos(ω_d t) + (ζω/ω_d)·sin(ω_d t) ),  avec ω_d = ω·√(1−ζ²)
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    public SpringEase()
    {
        // Le ressort EST la courbe finale : on neutralise le repli EaseOut de la base.
        EasingMode = EasingMode.EaseIn;
    }

    /// <summary>Taux d'amortissement ζ. &lt;1 = rebond, =1 = critique (aucun dépassement).</summary>
    public static readonly DependencyProperty DampingProperty =
        DependencyProperty.Register(nameof(Damping), typeof(double), typeof(SpringEase),
            new PropertyMetadata(0.7));

    public double Damping
    {
        get => (double)GetValue(DampingProperty);
        set => SetValue(DampingProperty, value);
    }

    /// <summary>Pulsation ω (rigidité). Plus élevée = ressort plus « serré » / rapide.</summary>
    public static readonly DependencyProperty StiffnessProperty =
        DependencyProperty.Register(nameof(Stiffness), typeof(double), typeof(SpringEase),
            new PropertyMetadata(10.0));

    public double Stiffness
    {
        get => (double)GetValue(StiffnessProperty);
        set => SetValue(StiffnessProperty, value);
    }

    protected override double EaseInCore(double t)
    {
        if (t <= 0.0) return 0.0;
        if (t >= 1.0) return 1.0;   // fin nette : la valeur tenue == To exact

        double zeta = Math.Clamp(Damping, 0.0001, 4.0);
        double w    = Stiffness;

        if (zeta < 1.0)
        {
            // Sous-amorti : un (ou plusieurs) dépassements puis stabilisation.
            double wd = w * Math.Sqrt(1.0 - zeta * zeta);
            return 1.0 - Math.Exp(-zeta * w * t)
                       * (Math.Cos(wd * t) + (zeta * w / wd) * Math.Sin(wd * t));
        }

        // Amorti critique : montée la plus rapide SANS aucun dépassement.
        return 1.0 - Math.Exp(-w * t) * (1.0 + w * t);
    }

    protected override Freezable CreateInstanceCore() => new SpringEase();
}
