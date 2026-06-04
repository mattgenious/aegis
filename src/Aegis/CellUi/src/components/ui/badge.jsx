import { cva } from 'class-variance-authority';
import { cn } from '../../lib/utils';

const badgeVariants = cva('badge', {
  variants: {
    tone: {
      neutral: 'badge-neutral',
      success: 'badge-success',
      danger: 'badge-danger',
      info: 'badge-info',
      warning: 'badge-warning'
    }
  },
  defaultVariants: {
    tone: 'neutral'
  }
});

export function Badge({ children, className, tone = 'neutral' }) {
  return <span className={cn(badgeVariants({ tone }), className)}>{children || 'unknown'}</span>;
}
