import {
  THEME_DEFAULT,
  UI_FONT_SCALE_DEFAULT,
  UI_FONT_SCALE_MAX,
  UI_FONT_SCALE_MIN,
  useTheme,
  useUiFontScale,
  type Theme,
} from "@features/settings/settings";
import { SegmentedControl } from "@shared/ui/SegmentedControl/SegmentedControl";
import { Field, SectionHeader, Stepper } from "./FormPrimitives";

export function AppearanceSection() {
  const [uiScale, setUiScale] = useUiFontScale();
  const [theme, setTheme] = useTheme();

  return (
    <>
      <SectionHeader title="Appearance · 外观" />
      <Field
        label="Theme"
        hint="Ink on paper, or paper on ink"
        onReset={theme !== THEME_DEFAULT ? () => setTheme(THEME_DEFAULT) : undefined}
      >
        <SegmentedControl<Theme>
          value={theme}
          onChange={setTheme}
          options={[
            { value: "light", label: "Light" },
            { value: "dark", label: "Dark" },
          ]}
        />
      </Field>
      <Field
        label="UI font size"
        hint={`${UI_FONT_SCALE_MIN}–${UI_FONT_SCALE_MAX}%`}
        onReset={
          uiScale !== UI_FONT_SCALE_DEFAULT ? () => setUiScale(UI_FONT_SCALE_DEFAULT) : undefined
        }
      >
        <Stepper
          value={uiScale}
          min={UI_FONT_SCALE_MIN}
          max={UI_FONT_SCALE_MAX}
          step={5}
          onChange={setUiScale}
          ariaLabelDown="Decrease UI font size"
          ariaLabelUp="Increase UI font size"
        />
      </Field>
    </>
  );
}
