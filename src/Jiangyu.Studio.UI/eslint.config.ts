import js from "@eslint/js";
import { defineConfig, globalIgnores } from "eslint/config";
import tseslint from "typescript-eslint";
import reactHooks from "eslint-plugin-react-hooks";
import reactRefresh from "eslint-plugin-react-refresh";
import eslintReact from "@eslint-react/eslint-plugin";
import jsxA11y from "eslint-plugin-jsx-a11y";
import importX from "eslint-plugin-import-x";
import vitest from "@vitest/eslint-plugin";
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
  eslintReact.configs["recommended-typescript"],
  reactHooks.configs.flat.recommended,
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
      parserOptions: {
        projectService: {
          allowDefaultProject: ["eslint.config.ts", "scripts/*.ts"],
        },
      },
    },
    plugins: {
      "react-refresh": reactRefresh,
      "import-x": importX,
    },
    rules: {
      "react-refresh/only-export-components": ["warn", { allowConstantExport: true }],

      // Circular imports cause `undefined`-at-import-time bugs that are
      // silent until a code path hits the partial export. Static detection
      // is the cheap path.
      "import-x/no-cycle": ["error", { maxDepth: 10 }],

      // Discriminated-union switches must cover every case; adding a new
      // variant without updating callers is a real bug source.
      "@typescript-eslint/switch-exhaustiveness-check": [
        "error",
        { considerDefaultExhaustiveForUnions: true },
      ],

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
    plugins: { vitest },
    rules: {
      // Tests can stub anything; loosen the strict-type-checked rules that
      // matter most for production code but get in the way of test fixtures.
      "@typescript-eslint/no-unsafe-assignment": "off",
      "@typescript-eslint/no-unsafe-member-access": "off",
      "@typescript-eslint/no-unsafe-call": "off",
      "@typescript-eslint/no-unsafe-argument": "off",
      "@typescript-eslint/no-unsafe-return": "off",
      "@typescript-eslint/no-non-null-assertion": "off",

      // Catch the test-file footguns that survive code review.
      // - no-focused-tests: `.only` left in skips the rest of the suite.
      // - no-disabled-tests: `.skip` rots silently.
      // - valid-expect: missing `await` on `expect(...).resolves.X` makes
      //   the assertion never run and the test pass on lies.
      "vitest/no-focused-tests": "error",
      "vitest/no-disabled-tests": "warn",
      "vitest/valid-expect": "error",
    },
  },
]);
