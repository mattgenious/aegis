import { cn } from '../../lib/utils';

export function Card({ children, className, asButton = false, onClick }) {
  const classes = cn('card', asButton && 'click-card', className);
  if (asButton) {
    return (
      <button className={classes} type="button" onClick={onClick}>
        {children}
      </button>
    );
  }

  return <article className={classes}>{children}</article>;
}
