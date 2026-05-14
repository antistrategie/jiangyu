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
      { text: "Concepts", link: "/concepts/" },
      { text: "Assets", link: "/assets/" },
      { text: "Templates", link: "/templates" },
      { text: "Studio", link: "/studio" },
      { text: "Troubleshooting", link: "/troubleshooting" },
    ],

    sidebar: [
      {
        text: "Introduction",
        items: [
          { text: "What is Jiangyu?", link: "/what-is-jiangyu" },
          { text: "Getting started", link: "/getting-started" },
        ],
      },
      {
        text: "Concepts",
        items: [
          { text: "Overview", link: "/concepts/" },
        ],
      },
      {
        text: "Assets",
        items: [
          { text: "Overview", link: "/assets/" },
          {
            text: "Replacements",
            collapsed: false,
            items: [
              { text: "Overview", link: "/assets/replacements/" },
              { text: "Textures", link: "/assets/replacements/textures" },
              { text: "Sprites", link: "/assets/replacements/sprites" },
              { text: "Models", link: "/assets/replacements/models" },
              { text: "Audio", link: "/assets/replacements/audio" },
            ],
          },
          {
            text: "Additions",
            collapsed: false,
            items: [
              { text: "Overview", link: "/assets/additions/" },
              { text: "Sprites", link: "/assets/additions/sprites" },
              { text: "Textures", link: "/assets/additions/textures" },
              { text: "Audio", link: "/assets/additions/audio" },
              { text: "Prefabs", link: "/assets/additions/prefabs" },
            ],
          },
        ],
      },
      {
        text: "Templates",
        items: [
          { text: "Templates (KDL)", link: "/templates" },
        ],
      },
      {
        text: "Studio",
        items: [
          { text: "Overview", link: "/studio" },
          {
            text: "AI agent",
            collapsed: false,
            items: [
              { text: "Overview", link: "/studio/agent" },
              { text: "Available tools", link: "/studio/agent-tools" },
            ],
          },
        ],
      },
      {
        text: "Reference",
        items: [
          { text: "Manifest (jiangyu.json)", link: "/reference/manifest" },
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
