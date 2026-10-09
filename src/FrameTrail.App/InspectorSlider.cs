using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace FrameTrail.App;

/// <summary>A numeric editor commits one undo transaction for drag, keyboard or accessibility edits.</summary>
public sealed class InspectorSlider : Slider
{
    public event EventHandler? AutomationValueCommitted;
    protected override AutomationPeer OnCreateAutomationPeer() => new InspectorSliderPeer(this);

    private sealed class InspectorSliderPeer(InspectorSlider owner) : SliderAutomationPeer(owner), IRangeValueProvider
    {
        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);
        bool IRangeValueProvider.IsReadOnly => false;
        double IRangeValueProvider.LargeChange => owner.LargeChange;
        double IRangeValueProvider.SmallChange => owner.SmallChange;
        double IRangeValueProvider.Maximum => owner.Maximum;
        double IRangeValueProvider.Minimum => owner.Minimum;
        double IRangeValueProvider.Value => owner.Value;
        void IRangeValueProvider.SetValue(double value)
        {
            if (!owner.IsEnabled) throw new ElementNotEnabledException();
            if (!double.IsFinite(value) || value < owner.Minimum || value > owner.Maximum)
                throw new ArgumentOutOfRangeException(nameof(value));
            owner.SetCurrentValue(ValueProperty, value);
            // The commit rebuilds the inspector. Finish the provider call before replacing its UI tree.
            owner.Dispatcher.BeginInvoke(() => owner.AutomationValueCommitted?.Invoke(owner, EventArgs.Empty));
        }
    }
}
