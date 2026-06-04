import { cva } from 'class-variance-authority';
import { cn } from '../../lib/utils';

const buttonVariants = cva('button', {
  variants: {
    variant: {
      default: '',
      ghost: 'ghost'
    },
    size: {
      default: '',
      fit: 'fit'
    },
    selected: {
      true: 'selected',
      false: ''
    }
  },
  defaultVariants: {
    variant: 'default',
    size: 'default',
    selected: false
  }
});

export function Button({ className, variant = 'default', size = 'default', selected = false, ...props }) {
  return <button className={cn(buttonVariants({ variant, size, selected }), className)} {...props} />;
}
