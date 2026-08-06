"use client";

// PSX local modifications (CP2):
// - Upstream types ToolHeader/ToolInput/ToolOutput against the AI SDK's
//   ToolUIPart; PSX does not ship the `ai` package, so the state/input/output
//   shapes are declared locally (same wire values, ACP-driven).
// - Upstream renders parameters/results through the AI Elements CodeBlock
//   (Shiki); PSX keeps its existing HTML `pre > code` pipeline, so this port
//   renders plain <pre><code> and CP4 styles it to match.
// - CP4: ToolHeader accepts `badge` (overrides the default status label with
//   PSX's ACP tool-state wording) and `titleClassName` (semantic anchor
//   classes for tests/replay tooling).

import { Badge } from "@/components/ui/badge";
import {
  Collapsible,
  CollapsibleContent,
  CollapsibleTrigger,
} from "@/components/ui/collapsible";
import { cn } from "@/lib/utils";
import {
  CheckCircleIcon,
  ChevronDownIcon,
  CircleIcon,
  ClockIcon,
  WrenchIcon,
  XCircleIcon,
} from "lucide-react";
import type { ComponentProps, ReactNode } from "react";
import { isValidElement } from "react";

/** Local replacement for the AI SDK ToolUIPart state union. */
export type ToolState =
  | "input-streaming"
  | "input-available"
  | "approval-requested"
  | "approval-responded"
  | "output-available"
  | "output-error"
  | "output-denied";

export type ToolProps = ComponentProps<typeof Collapsible>;

export const Tool = ({ className, ...props }: ToolProps) => (
  <Collapsible
    className={cn("not-prose mb-4 w-full rounded-md border", className)}
    {...props}
  />
);

export type ToolHeaderProps = {
  title?: string;
  type: string;
  state: ToolState;
  className?: string;
  /** PSX: overrides the default status-badge label (icon still follows state). */
  badge?: ReactNode;
  /** PSX: extra classes for the title span (semantic anchors). */
  titleClassName?: string;
};

const getStatusBadge = (status: ToolState, label?: ReactNode) => {
  const labels: Record<ToolState, string> = {
    "input-streaming": "Pending",
    "input-available": "Running",
    "approval-requested": "Awaiting Approval",
    "approval-responded": "Responded",
    "output-available": "Completed",
    "output-error": "Error",
    "output-denied": "Denied",
  };

  const icons: Record<ToolState, ReactNode> = {
    "input-streaming": <CircleIcon className="size-4" />,
    "input-available": <ClockIcon className="size-4 animate-pulse" />,
    "approval-requested": <ClockIcon className="size-4 text-yellow-600" />,
    "approval-responded": <CheckCircleIcon className="size-4 text-blue-600" />,
    "output-available": <CheckCircleIcon className="size-4 text-green-600" />,
    "output-error": <XCircleIcon className="size-4 text-red-600" />,
    "output-denied": <XCircleIcon className="size-4 text-orange-600" />,
  };

  return (
    <Badge className="gap-1.5 rounded-full text-xs" variant="secondary">
      {icons[status]}
      {label ?? labels[status]}
    </Badge>
  );
};

export const ToolHeader = ({
  className,
  title,
  type,
  state,
  badge,
  titleClassName,
  ...props
}: ToolHeaderProps) => (
  <CollapsibleTrigger
    className={cn(
      "flex w-full items-center justify-between gap-4 p-3 outline-none focus-visible:outline-2 focus-visible:outline-solid focus-visible:outline-[var(--agent-focus-ring)] focus-visible:outline-offset-2",
      className
    )}
    {...props}
  >
    <div className="flex items-center gap-2">
      <WrenchIcon className="size-4 text-muted-foreground" />
      <span className={cn("font-medium text-sm", titleClassName)}>
        {title ?? type.split("-").slice(1).join("-")}
      </span>
      {getStatusBadge(state, badge)}
    </div>
    <ChevronDownIcon className="size-4 text-muted-foreground transition-transform group-data-[state=open]:rotate-180" />
  </CollapsibleTrigger>
);

export type ToolContentProps = ComponentProps<typeof CollapsibleContent>;

export const ToolContent = ({ className, ...props }: ToolContentProps) => (
  <CollapsibleContent
    className={cn(
      "data-[state=closed]:fade-out-0 data-[state=closed]:slide-out-to-top-2 data-[state=open]:slide-in-from-top-2 text-popover-foreground outline-none data-[state=closed]:animate-out data-[state=open]:animate-in",
      className
    )}
    {...props}
  />
);

/** PSX replacement for the AI Elements CodeBlock: the fenced-code HTML
 * pipeline (`pre > code`) styled by CSS, no Shiki. */
const PlainCode = ({ code }: { code: string }) => (
  <pre className="agent-native-scroll overflow-x-auto p-3 text-xs">
    <code>{code}</code>
  </pre>
);

export type ToolInputProps = ComponentProps<"div"> & {
  input: unknown;
};

export const ToolInput = ({ className, input, ...props }: ToolInputProps) => (
  <div className={cn("space-y-2 overflow-hidden p-4", className)} {...props}>
    <h4 className="font-medium text-muted-foreground text-xs uppercase tracking-wide">
      Parameters
    </h4>
    <div className="rounded-md bg-muted/50">
      <PlainCode code={JSON.stringify(input, null, 2)} />
    </div>
  </div>
);

export type ToolOutputProps = ComponentProps<"div"> & {
  output: unknown;
  errorText: string | undefined;
};

export const ToolOutput = ({
  className,
  output,
  errorText,
  ...props
}: ToolOutputProps) => {
  if (!(output || errorText)) {
    return null;
  }

  let Output = <div>{output as ReactNode}</div>;

  if (typeof output === "object" && !isValidElement(output)) {
    Output = <PlainCode code={JSON.stringify(output, null, 2)} />;
  } else if (typeof output === "string") {
    Output = <PlainCode code={output} />;
  }

  return (
    <div className={cn("space-y-2 p-4", className)} {...props}>
      <h4 className="font-medium text-muted-foreground text-xs uppercase tracking-wide">
        {errorText ? "Error" : "Result"}
      </h4>
      <div
        className={cn(
          "agent-native-scroll overflow-x-auto rounded-md text-xs [&_table]:w-full",
          errorText
            ? "bg-destructive/10 text-destructive"
            : "bg-muted/50 text-foreground"
        )}
      >
        {errorText && <div>{errorText}</div>}
        {Output}
      </div>
    </div>
  );
};
