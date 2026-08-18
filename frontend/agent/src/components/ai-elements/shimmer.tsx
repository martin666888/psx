"use client";

// PSX local modification (CP2): the upstream Shimmer animates with
// motion/react. PSX does not ship the motion package (bundle budget), so the
// same visual is reproduced with a pure CSS keyframe animation defined in
// css/tailwind.css (@keyframes psx-shimmer). Public API is unchanged.

import { cn } from "@/lib/utils";
import { type CSSProperties, type ElementType, memo, useMemo } from "react";

export type TextShimmerProps = {
  children: string;
  as?: ElementType;
  className?: string;
  duration?: number;
  spread?: number;
};

const ShimmerComponent = ({
  children,
  as: Component = "p",
  className,
  duration = 2,
  spread = 2,
}: TextShimmerProps) => {
  const dynamicSpread = useMemo(
    () => (children?.length ?? 0) * spread,
    [children, spread]
  );

  return (
    <Component
      className={cn(
        "psx-shimmer relative inline-block bg-[length:250%_100%,auto] bg-clip-text text-transparent",
        // PSX local modification: upstream references var(--color-*) theme
        // variables, but this project's "@theme inline" setup does not emit
        // runtime --color-* variables (raw shadcn vars live on .agent-ui and
        // are what the Theme Adapter updates), so the raw vars are used here.
        "[--bg:linear-gradient(90deg,#0000_calc(50%-var(--spread)),var(--background),#0000_calc(50%+var(--spread)))] [background-repeat:no-repeat,padding-box]",
        className
      )}
      style={
        {
          "--spread": `${dynamicSpread}px`,
          "--psx-shimmer-duration": `${duration}s`,
          backgroundImage:
            "var(--bg), linear-gradient(var(--muted-foreground), var(--muted-foreground))",
        } as CSSProperties
      }
    >
      {children}
    </Component>
  );
};

export const Shimmer = memo(ShimmerComponent);
