import 'katex/dist/katex.min.css';
import { createMathPlugin } from '@streamdown/math';

export const mathPlugin = createMathPlugin({
  singleDollarTextMath: true,
  errorColor: 'var(--destructive)'
});
