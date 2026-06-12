import { AI_ENABLED_DEFAULT, useAiEnabled } from "@features/settings/settings";
import { SegmentedControl } from "@shared/ui/SegmentedControl/SegmentedControl";
import { Field, SectionHeader } from "./FormPrimitives";

export function AiSection() {
  const [aiEnabled, setAiEnabled] = useAiEnabled();

  return (
    <>
      <SectionHeader title="AI · 智能" />
      <Field
        label="Enable AI features"
        hint="Opt-in to AI-powered features such as the agent panel."
        onReset={
          aiEnabled !== AI_ENABLED_DEFAULT ? () => setAiEnabled(AI_ENABLED_DEFAULT) : undefined
        }
      >
        <SegmentedControl<"on" | "off">
          value={aiEnabled ? "on" : "off"}
          onChange={(v) => setAiEnabled(v === "on")}
          options={[
            { value: "on", label: "On" },
            { value: "off", label: "Off" },
          ]}
        />
      </Field>
    </>
  );
}
