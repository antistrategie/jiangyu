import js from "@eslint/js";
import { defineConfig, globalIgnores } from "eslint/config";
import tseslint from "typescript-eslint";
import reactHooks from "eslint-plugin-react-hooks";
import reactRefresh from "eslint-plugin-react-refresh";
import jsxA11y from "eslint-plugin-jsx-a11y";
import globals from "globals";

export default defineConfig([
  globalIgnores([
    "dist/**",
    "generated/**",
    "node_modules/**",
    "**/*.d.ts",
    "vite.config.ts",
    "vitest.config.ts",
  ]),
  {
    extends: [
      js.configs.recommended,
      tseslint.configs.strictTypeChecked,
      tseslint.configs.stylisticTypeChecked,
      jsxA11y.flatConfigs.recommended,
    ],
    languageOptions: {
      ecmaVersion: 2023,
      globals: { ...globals.browser },
      // projectService auto-discovers the nearest tsconfig from each file's
      // location. allowDefaultProject lets us lint files that aren't covered
      // by the app tsconfig (e.g. this config file).
      parserOptions: {
        projectService: {
          allowDefaultProject: ["eslint.config.ts"],
        },
      },
    },
    plugins: {
      "react-hooks": reactHooks,
      "react-refresh": reactRefresh,
    },
    rules: {
      ...reactHooks.configs.recommended.rules,
      "react-refresh/only-export-components": ["warn", { allowConstantExport: true }],

      // Catch console.log left behind in shipped code; warn (not error) so
      // intentional console.warn / console.error during dev still passes.
      "no-console": ["warn", { allow: ["warn", "error"] }],

      // tsconfig already enforces noUnusedLocals / noUnusedParameters; defer
      // to TS for those instead of double-reporting.
      "@typescript-eslint/no-unused-vars": "off",
      "no-unused-vars": "off",

      // Floating promises are real bugs; require `void` or `await` on every
      // promise expression that isn't deliberately fire-and-forget.
      "@typescript-eslint/no-floating-promises": "error",

      // Allow short-circuit/ternary returns in handlers that don't need their
      // value, but keep the strict rule active for misuse elsewhere.
      "@typescript-eslint/no-confusing-void-expression": [
        "error",
        { ignoreArrowShorthand: true, ignoreVoidOperator: true },
      ],

      // Numbers and booleans have unambiguous toString(); the rule's actual
      // purpose is catching `${someObject}` rendering "[object Object]".
      "@typescript-eslint/restrict-template-expressions": [
        "error",
        { allowNumber: true, allowBoolean: true },
      ],

      // No-op arrow handlers like `onLeave={() => {}}` are a legitimate React
      // idiom; the rule still catches accidentally-empty function declarations.
      "@typescript-eslint/no-empty-function": ["error", { allow: ["arrowFunctions"] }],
    },
  },
  {
    files: ["**/*.test.ts", "**/*.test.tsx"],
    rules: {
      // Tests can stub anything; loosen the strict-type-checked rules that
      // matter most for production code but get in the way of test fixtures.
      "@typescript-eslint/no-unsafe-assignment": "off",
      "@typescript-eslint/no-unsafe-member-access": "off",
      "@typescript-eslint/no-unsafe-call": "off",
      "@typescript-eslint/no-unsafe-argument": "off",
      "@typescript-eslint/no-unsafe-return": "off",
      "@typescript-eslint/no-non-null-assertion": "off",
    },
  },
]);
