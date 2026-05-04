import { defineConfig } from "vitepress";
import kdlGrammar from "./grammars/kdl.tmLanguage.json" with { type: "json" };

export default defineConfig({
  title: "绛雨 Jiangyu",
  description: "Modkit for MENACE",
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
      { text: "Get started", link: "/getting-started" },
      { text: "Reference", link: "/reference/studio" },
      { text: "Troubleshooting", link: "/troubleshooting" },
    ],

    sidebar: [
      {
        text: "Start here",
        items: [
          { text: "What is Jiangyu?", link: "/what-is-jiangyu" },
          { text: "Getting started", link: "/getting-started" },
        ],
      },
      {
        text: "Reference",
        items: [
          { text: "Studio", link: "/reference/studio" },
          {
            text: "Replacements",
            collapsed: false,
            items: [
              { text: "Textures", link: "/reference/replacements/textures" },
              { text: "Sprites", link: "/reference/replacements/sprites" },
              { text: "Models", link: "/reference/replacements/models" },
              { text: "Audio", link: "/reference/replacements/audio" },
            ],
          },
          { text: "Templates (KDL)", link: "/reference/templates" },
          { text: "Manifest (jiangyu.json)", link: "/reference/manifest" },
          {
            text: "AI Agent",
            collapsed: false,
            items: [
              { text: "Overview", link: "/reference/ai/" },
              { text: "Available tools", link: "/reference/ai/tools" },
            ],
          },
          { text: "CLI", link: "/reference/cli" },
        ],
      },
      {
        text: "Help",
        items: [{ text: "Troubleshooting", link: "/troubleshooting" }],
      },
    ],

    socialLinks: [
      { icon: "github", link: "https://github.com/antistrategie/jiangyu" },
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
