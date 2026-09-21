import React from "react";

// The source dashboard renders Heroicons Solid glyphs, embedded verbatim in Components/Icon.razor
// (see docs/gui/DESIGN.md §4.3). This mockup fetches the same icon set from the Heroicons npm
// package's flat CDN layout instead of embedding path data, recolored with a CSS mask-image so it
// always matches the current text color.
const SLUG = {
  settings: "cog-6-tooth",
  search: "magnifying-glass",
  close: "x-mark",
  "chevron-down": "chevron-down",
  "chevron-right": "chevron-right",
  "alert-triangle": "exclamation-triangle",
  info: "information-circle",
  "check-circle": "check-circle",
  "alert-circle": "exclamation-circle",
  activity: "signal",
  "trend-up": "arrow-trending-up",
  bot: "cpu-chip",
};

export function Icon({ name, size = 16, color = "var(--text-secondary)", style, ...rest }) {
  // "dot" has no Heroicons Solid equivalent (a bare filled circle isn't a glyph in that set) - render
  // it as a plain CSS-filled circle instead of faking a mask-image fetch.
  if (name === "dot") {
    return (
      <span
        role="img"
        aria-label={name}
        style={{
          display: "inline-block",
          width: size,
          height: size,
          borderRadius: "50%",
          backgroundColor: color,
          flexShrink: 0,
          ...style,
        }}
        {...rest}
      />
    );
  }

  const slug = SLUG[name] || name;
  const url = `https://unpkg.com/heroicons@2.1.5/24/solid/${slug}.svg`;
  return (
    <span
      role="img"
      aria-label={name}
      style={{
        display: "inline-block",
        width: size,
        height: size,
        backgroundColor: color,
        WebkitMaskImage: `url(${url})`,
        maskImage: `url(${url})`,
        WebkitMaskSize: "contain",
        maskSize: "contain",
        WebkitMaskRepeat: "no-repeat",
        maskRepeat: "no-repeat",
        WebkitMaskPosition: "center",
        maskPosition: "center",
        flexShrink: 0,
        ...style,
      }}
      {...rest}
    />
  );
}
