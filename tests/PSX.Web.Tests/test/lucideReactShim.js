import { createElement, forwardRef } from 'react';

function createTestIcon(name) {
  const Icon = forwardRef(function TestLucideIcon(
    {
      size = 24,
      color = 'currentColor',
      strokeWidth = 2,
      className = '',
      children,
      ...props
    },
    ref
  ) {
    return createElement(
      'svg',
      {
        ref,
        ...props,
        xmlns: 'http://www.w3.org/2000/svg',
        width: size,
        height: size,
        viewBox: '0 0 24 24',
        fill: 'none',
        stroke: color,
        strokeWidth,
        strokeLinecap: 'round',
        strokeLinejoin: 'round',
        className: ['lucide', `lucide-${name}`, className].filter(Boolean).join(' '),
        'data-lucide': name
      },
      createElement('path', { d: 'M4 12h16' }),
      children
    );
  });
  Icon.displayName = name;
  return Icon;
}

export const ArrowDownIcon = createTestIcon('arrow-down');
export const BrainIcon = createTestIcon('brain');
export const ChartColumnIcon = createTestIcon('chart-column');
export const CheckCircle2Icon = createTestIcon('circle-check');
export const CheckCircleIcon = createTestIcon('circle-check-big');
export const CheckIcon = createTestIcon('check');
export const ChevronDownIcon = createTestIcon('chevron-down');
export const ChevronRightIcon = createTestIcon('chevron-right');
export const ChevronUpIcon = createTestIcon('chevron-up');
export const CircleDotIcon = createTestIcon('circle-dot');
export const CircleIcon = createTestIcon('circle');
export const ClockIcon = createTestIcon('clock');
export const CopyIcon = createTestIcon('copy');
export const CornerDownLeftIcon = createTestIcon('corner-down-left');
export const FolderIcon = createTestIcon('folder');
export const FolderOpenIcon = createTestIcon('folder-open');
export const ImageIcon = createTestIcon('image');
export const InfoIcon = createTestIcon('info');
export const Loader2Icon = createTestIcon('loader-circle');
export const LoaderCircleIcon = createTestIcon('loader-circle');
export const MicIcon = createTestIcon('mic');
export const PaperclipIcon = createTestIcon('paperclip');
export const PlusIcon = createTestIcon('plus');
export const RefreshCwIcon = createTestIcon('refresh-cw');
export const SearchIcon = createTestIcon('search');
export const SquareIcon = createTestIcon('square');
export const TriangleAlertIcon = createTestIcon('triangle-alert');
export const WrenchIcon = createTestIcon('wrench');
export const XCircleIcon = createTestIcon('circle-x');
export const XIcon = createTestIcon('x');
