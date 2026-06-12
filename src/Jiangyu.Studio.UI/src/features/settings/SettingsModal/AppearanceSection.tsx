import {
  UI_FONT_SCALE_DEFAULT,
  UI_FONT_SCALE_MAX,
  UI_FONT_SCALE_MIN,
  useUiFontScale,
} from "@features/settings/settings";
import { Field, SectionHeader, Stepper } from "./FormPrimitives";

export function AppearanceSection() {
  const [uiScale, setUiScale] = useUiFontScale();

  return (
    <>
      <SectionHeader title="Appearance · 外观" />
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
