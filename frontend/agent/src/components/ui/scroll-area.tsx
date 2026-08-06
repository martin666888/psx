import * as React from "react"
import * as ScrollAreaPrimitive from "@radix-ui/react-scroll-area"

import { cn } from "@/lib/utils"

// PSX additions over the stock shadcn wrapper: callers may address the
// scrollable viewport directly (ref for scrollTop control, className and
// data-* anchors for CSS/test contracts) because PSX controllers treat the
// scroll node as a semantic anchor. The thumb follows the global
// --agent-scrollbar theme token and the 8px ladder used by the webkit
// scrollbar skin so both scrollbar families look identical.
function ScrollArea({
  className,
  children,
  viewportProps,
  ...props
}: React.ComponentProps<typeof ScrollAreaPrimitive.Root> & {
  viewportProps?: React.ComponentProps<typeof ScrollAreaPrimitive.Viewport> & {
    [dataAttribute: `data-${string}`]: string
  }
}) {
  const { className: viewportClassName, ...viewportRest } = viewportProps ?? {}
  return (
    <ScrollAreaPrimitive.Root
      data-slot="scroll-area"
      className={cn("relative", className)}
      {...props}
    >
      <ScrollAreaPrimitive.Viewport
        data-slot="scroll-area-viewport"
        className={cn(
          "size-full rounded-[inherit] transition-[color,box-shadow] outline-none focus-visible:outline-2 focus-visible:outline-solid focus-visible:outline-[var(--agent-focus-ring)] focus-visible:outline-offset-2",
          viewportClassName
        )}
        {...viewportRest}
      >
        {children}
      </ScrollAreaPrimitive.Viewport>
      <ScrollBar />
      <ScrollAreaPrimitive.Corner />
    </ScrollAreaPrimitive.Root>
  )
}

function ScrollBar({
  className,
  orientation = "vertical",
  ...props
}: React.ComponentProps<typeof ScrollAreaPrimitive.ScrollAreaScrollbar>) {
  return (
    <ScrollAreaPrimitive.ScrollAreaScrollbar
      data-slot="scroll-area-scrollbar"
      orientation={orientation}
      className={cn(
        "flex touch-none p-px transition-colors select-none",
        orientation === "vertical" &&
          "h-full w-2 border-l border-l-transparent",
        orientation === "horizontal" &&
          "h-2 flex-col border-t border-t-transparent",
        className
      )}
      {...props}
    >
      <ScrollAreaPrimitive.ScrollAreaThumb
        data-slot="scroll-area-thumb"
        className="relative flex-1 rounded-full bg-[var(--agent-scrollbar)]"
      />
    </ScrollAreaPrimitive.ScrollAreaScrollbar>
  )
}

export { ScrollArea, ScrollBar }
