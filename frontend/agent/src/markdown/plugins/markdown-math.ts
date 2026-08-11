import 'katex/dist/katex.min.css';
import { createMathPlugin } from '@streamdown/math';

export const mathPlugin = createMathPlugin({
  singleDollarTextMath: false,
  errorColor: 'var(--destructive)'
});
