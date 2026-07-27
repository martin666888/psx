// lab/main.tsx — Debug-only Component Lab entry (dev server only; never part
// of the production build input). Renders the vendored shadcn primitives and
// AI Elements under the .agent-ui variable boundary so visual parity with the
// upstream components and the Theme Adapter mapping can be checked by hand.

import { StrictMode, useState } from 'react';
import { createRoot } from 'react-dom/client';
import '../css/tailwind.css';
import { applyShadcnTheme } from '@/ui/themeAdapter.js';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { Badge } from '@/components/ui/badge';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from '@/components/ui/popover';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from '@/components/ui/dialog';
import { ScrollArea } from '@/components/ui/scroll-area';
import {
  Message,
  MessageContent,
} from '@/components/ai-elements/message';
import {
  Reasoning,
  ReasoningContent,
  ReasoningTrigger,
} from '@/components/ai-elements/reasoning';
import {
  Tool,
  ToolContent,
  ToolHeader,
  ToolInput,
  ToolOutput,
} from '@/components/ai-elements/tool';
import { Loader } from '@/components/ai-elements/loader';
import { Shimmer } from '@/components/ai-elements/shimmer';
import { Task, TaskContent, TaskItem, TaskTrigger } from '@/components/ai-elements/task';

// The sample payload mirrors theme-presets/vercel-neutral-dark.ini so the lab
// exercises the same Theme Adapter path production uses.
const SAMPLE_DARK = {
  themeColors: {
    background: '#0a0a0a',
    surface: '#141414',
    surfaceRaised: '#171717',
    surfaceMuted: '#1f1f1f',
    hover: '#262626',
    border: '#262626',
    borderStrong: '#404040',
    text: '#fafafa',
    textMuted: '#a3a3a3',
    textDim: '#737373',
    accent: '#ededed',
    accentHover: '#ffffff',
    error: '#ff6467',
    errorBg: '#2a1517',
  },
  agentThemeColors: {
    sendBtn: '#ededed',
    sendBtnHover: '#ffffff',
    sendBtnText: '#0a0a0a',
    focusRing: '#737373',
  },
};

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="space-y-3">
      <h2 className="font-semibold text-lg">{title}</h2>
      {children}
    </section>
  );
}

function Lab() {
  const [themed, setThemed] = useState(false);

  const toggleTheme = () => {
    const host = document.getElementById('lab-root');
    if (!host) return;
    if (themed) {
      host.classList.remove('agent-ui-dark');
      host.removeAttribute('style');
    } else {
      applyShadcnTheme(host, SAMPLE_DARK);
    }
    setThemed(!themed);
  };

  return (
    <div className="mx-auto max-w-3xl space-y-10 bg-background p-8 text-foreground">
      <header className="flex items-center justify-between">
        <h1 className="font-bold text-2xl">PSX Component Lab (Debug)</h1>
        <Button onClick={toggleTheme} variant="outline">
          {themed ? 'Reset to defaults' : 'Apply Vercel Dark via Theme Adapter'}
        </Button>
      </header>

      <Section title="Button">
        <div className="flex flex-wrap gap-2">
          <Button>Default</Button>
          <Button variant="secondary">Secondary</Button>
          <Button variant="outline">Outline</Button>
          <Button variant="ghost">Ghost</Button>
          <Button variant="destructive">Destructive</Button>
          <Button variant="link">Link</Button>
        </div>
      </Section>

      <Section title="Input / Textarea / Badge">
        <div className="flex max-w-sm flex-col gap-3">
          <Input placeholder="Type here..." />
          <Textarea placeholder="Multiline..." />
          <div className="flex gap-2">
            <Badge>Badge</Badge>
            <Badge variant="secondary">Secondary</Badge>
            <Badge variant="outline">Outline</Badge>
          </div>
        </div>
      </Section>

      <Section title="Select / Popover / Dialog / ScrollArea">
        <div className="flex flex-wrap items-center gap-3">
          <Select>
            <SelectTrigger className="w-44">
              <SelectValue placeholder="Pick one" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="one">Option one</SelectItem>
              <SelectItem value="two">Option two</SelectItem>
            </SelectContent>
          </Select>
          <Popover>
            <PopoverTrigger asChild>
              <Button variant="outline">Popover</Button>
            </PopoverTrigger>
            <PopoverContent>Popover content.</PopoverContent>
          </Popover>
          <Dialog>
            <DialogTrigger asChild>
              <Button variant="outline">Dialog</Button>
            </DialogTrigger>
            <DialogContent>
              <DialogHeader>
                <DialogTitle>Dialog title</DialogTitle>
                <DialogDescription>Dialog description.</DialogDescription>
              </DialogHeader>
            </DialogContent>
          </Dialog>
          <ScrollArea className="h-20 w-44 rounded-md border p-2 text-sm">
            {Array.from({ length: 12 }, (_, i) => (
              <p key={i}>Row {i + 1}</p>
            ))}
          </ScrollArea>
        </div>
      </Section>

      <Section title="AI Elements — Message">
        <div className="space-y-4">
          <Message from="user">
            <MessageContent>How do I run the tests?</MessageContent>
          </Message>
          <Message from="assistant">
            <MessageContent>
              Run <code>tools/test.ps1 -Suite Fast</code> from the repo root.
            </MessageContent>
          </Message>
        </div>
      </Section>

      <Section title="AI Elements — Reasoning / Shimmer / Loader">
        <Reasoning defaultOpen duration={3} isStreaming={false}>
          <ReasoningTrigger />
          <ReasoningContent>
            The user asked about tests; the Fast suite covers unit, frontend
            and the release gate.
          </ReasoningContent>
        </Reasoning>
        <div className="flex items-center gap-4">
          <Shimmer duration={1.5}>Streaming preview text...</Shimmer>
          <Loader size={20} />
        </div>
      </Section>

      <Section title="AI Elements — Tool / Task">
        <Tool defaultOpen>
          <ToolHeader state="output-available" title="read_file" type="tool-read_file" />
          <ToolContent>
            <ToolInput input={{ path: 'README.md' }} />
            <ToolOutput errorText={undefined} output={'# PSX\n...'} />
          </ToolContent>
        </Tool>
        <Task defaultOpen>
          <TaskTrigger title="Searching the codebase" />
          <TaskContent>
            <TaskItem>Scanning frontend/agent/src</TaskItem>
            <TaskItem>Found 3 matches</TaskItem>
          </TaskContent>
        </Task>
      </Section>
    </div>
  );
}

const host = document.getElementById('lab-root');
if (host) {
  host.classList.add('agent-ui');
  createRoot(host).render(
    <StrictMode>
      <Lab />
    </StrictMode>
  );
}
