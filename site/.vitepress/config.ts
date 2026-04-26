import { defineConfig } from "vitepress";

export default defineConfig({
  title: "Jiangyu",
  description: "Modkit for MENACE",
  lang: "en-GB",
  base: "/jiangyu/",
  cleanUrls: true,
  lastUpdated: true,

  head: [
    ["link", { rel: "icon", type: "image/png", sizes: "32x32", href: "/jiangyu/favicon-32.png" }],
    ["link", { rel: "icon", type: "image/png", sizes: "144x144", href: "/jiangyu/favicon.png" }],
  ],

  themeConfig: {
    nav: [
      { text: "Get started", link: "/getting-started" },
      { text: "Reference", link: "/reference/studio" },
      { text: "Troubleshooting", link: "/troubleshooting" },
    ],

    sidebar: [
      {
        text: "Start here",
        items: [
          { text: "Introduction", link: "/" },
          { text: "Getting started", link: "/getting-started" },
        ],
      },
      {
        text: "Reference",
        items: [
          { text: "Studio", link: "/reference/studio" },
          { text: "Manifest (jiangyu.json)", link: "/reference/manifest" },
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
