import { defineConfig } from "vitepress";
import kdlGrammar from "./grammars/kdl.tmLanguage.json" with { type: "json" };

export default defineConfig({
  title: "绛雨 Jiangyu",
  description: "Modding platform for MENACE",
  lang: "en-GB",
  base: "/jiangyu/",
  cleanUrls: true,
  lastUpdated: true,
  appearance: false,

  markdown: {
    languages: [kdlGrammar as never],
    theme: "github-dark",
  },

  head: [
    [
      "link",
      {
        rel: "icon",
        type: "image/png",
        sizes: "32x32",
        href: "/jiangyu/favicon-32.png",
      },
    ],
    [
      "link",
      {
        rel: "icon",
        type: "image/png",
        sizes: "144x144",
        href: "/jiangyu/favicon.png",
      },
    ],
  ],

  themeConfig: {
    notFound: {
      code: "404",
      title: "PAGE NOT FOUND",
      quote: "迷途知返。",
      linkText: "Return to base",
    },

    nav: [
      { text: "What is Jiangyu?", link: "/what-is-jiangyu" },
      { text: "Get started", link: "/tutorials/installation" },
      { text: "Guides", link: "/assets/replacements/" },
      { text: "Reference", link: "/reference/cli" },
    ],

    // Organised by the Diátaxis modes: a learning path (tutorials), task
    // recipes (how-to), the dry surface (reference), and the why (explanation).
    sidebar: [
      {
        text: "Tutorials",
        items: [
          { text: "Installation", link: "/tutorials/installation" },
          { text: "Your first patch", link: "/tutorials/first-patch" },
          { text: "Your first re-skin", link: "/tutorials/first-reskin" },
        ],
      },
      {
        text: "How-to guides",
        items: [
          {
            text: "Replace an asset",
            items: [
              { text: "Overview", link: "/assets/replacements/" },
              { text: "Textures", link: "/assets/replacements/textures" },
              { text: "Sprites", link: "/assets/replacements/sprites" },
              { text: "Audio", link: "/assets/replacements/audio" },
              { text: "Models", link: "/assets/replacements/models" },
            ],
          },
          {
            text: "Add an asset",
            items: [
              { text: "Overview", link: "/assets/additions/" },
              { text: "Sprites", link: "/assets/additions/sprites" },
              { text: "Textures", link: "/assets/additions/textures" },
              { text: "Audio", link: "/assets/additions/audio" },
              { text: "Prefabs", link: "/assets/additions/prefabs" },
            ],
          },
          { text: "Patch and clone templates", link: "/templates" },
          { text: "Write a custom template type", link: "/sdk/template-types" },
          { text: "Add UI", link: "/sdk/ui" },
          { text: "Translate a mod", link: "/localise" },
          { text: "Call game verbs", link: "/sdk/verbs" },
          { text: "Set up the Unity project", link: "/unity-project" },
          { text: "Use Studio", link: "/studio" },
          { text: "Use the AI agent", link: "/studio/agent" },
          { text: "Troubleshooting", link: "/troubleshooting" },
        ],
      },
      {
        text: "Reference",
        items: [
          { text: "CLI", link: "/reference/cli" },
          { text: "Manifest (jiangyu.json)", link: "/reference/manifest" },
          { text: "Game verbs", link: "/reference/verbs" },
          { text: "Hooks", link: "/reference/hooks" },
          { text: "Agent tools", link: "/studio/agent-tools" },
        ],
      },
      {
        text: "Explanation",
        items: [
          { text: "What is Jiangyu?", link: "/what-is-jiangyu" },
          { text: "Concepts", link: "/concepts/" },
          { text: "The SDK", link: "/sdk/" },
        ],
      },
    ],

    socialLinks: [
      { icon: "github", link: "https://github.com/antistrategie/jiangyu" },
      { icon: "discord", link: "https://discord.com/invite/XcfYGmxvde" },
    ],

    editLink: {
      pattern: "https://github.com/antistrategie/jiangyu/edit/main/site/:path",
      text: "Edit this page on GitHub",
    },

    search: {
      provider: "local",
    },

    outline: {
      level: [2, 3],
    },
  },
});
