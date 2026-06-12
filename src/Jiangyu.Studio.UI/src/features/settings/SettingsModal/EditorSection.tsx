import {
  EDITOR_FONT_SIZE_DEFAULT,
  EDITOR_FONT_SIZE_MAX,
  EDITOR_FONT_SIZE_MIN,
  EDITOR_KEYBIND_MODE_DEFAULT,
  EDITOR_WORD_WRAP_DEFAULT,
  TEMPLATE_EDITOR_MODE_DEFAULT,
  useEditorFontSize,
  useEditorKeybindMode,
  useEditorWordWrap,
  useTemplateEditorMode,
  type EditorKeybindMode,
  type EditorWordWrap,
  type TemplateEditorMode,
} from "@features/settings/settings";
import { SegmentedControl } from "@shared/ui/SegmentedControl/SegmentedControl";
import { Field, SectionHeader, Stepper } from "./FormPrimitives";

export function EditorSection() {
  const [fontSize, setFontSize] = useEditorFontSize();
  const [wordWrap, setWordWrap] = useEditorWordWrap();
  const [keybinds, setKeybinds] = useEditorKeybindMode();
  const [templateMode, setTemplateMode] = useTemplateEditorMode();

  return (
    <>
      <SectionHeader title="Editor · 编辑器" />
      <Field
        label="Font size"
        hint={`${EDITOR_FONT_SIZE_MIN}–${EDITOR_FONT_SIZE_MAX}px`}
        onReset={
          fontSize !== EDITOR_FONT_SIZE_DEFAULT
            ? () => setFontSize(EDITOR_FONT_SIZE_DEFAULT)
            : undefined
        }
      >
        <Stepper
          value={fontSize}
          min={EDITOR_FONT_SIZE_MIN}
          max={EDITOR_FONT_SIZE_MAX}
          onChange={setFontSize}
          ariaLabelDown="Decrease editor font size"
          ariaLabelUp="Increase editor font size"
        />
      </Field>
      <Field
        label="Word wrap"
        onReset={
          wordWrap !== EDITOR_WORD_WRAP_DEFAULT
            ? () => setWordWrap(EDITOR_WORD_WRAP_DEFAULT)
            : undefined
        }
      >
        <SegmentedControl<EditorWordWrap>
          value={wordWrap}
          onChange={setWordWrap}
          options={[
            { value: "on", label: "On" },
            { value: "off", label: "Off" },
          ]}
        />
      </Field>
      <Field
        label="Keybinds"
        onReset={
          keybinds !== EDITOR_KEYBIND_MODE_DEFAULT
            ? () => setKeybinds(EDITOR_KEYBIND_MODE_DEFAULT)
            : undefined
        }
      >
        <SegmentedControl<EditorKeybindMode>
          value={keybinds}
          onChange={setKeybinds}
          options={[
            { value: "default", label: "Default" },
            { value: "vim", label: "Vim" },
          ]}
        />
      </Field>
      <Field
        label="Template editor"
        hint="Default mode for .kdl template files"
        onReset={
          templateMode !== TEMPLATE_EDITOR_MODE_DEFAULT
            ? () => setTemplateMode(TEMPLATE_EDITOR_MODE_DEFAULT)
            : undefined
        }
      >
        <SegmentedControl<TemplateEditorMode>
          value={templateMode}
          onChange={setTemplateMode}
          options={[
            { value: "visual", label: "Visual" },
            { value: "source", label: "Source" },
          ]}
        />
      </Field>
    </>
  );
}
