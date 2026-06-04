import * as RadixTabs from '@radix-ui/react-tabs';
import { cn } from '../../lib/utils';

export function Tabs({ tabs, value, onChange, className }) {
  return (
    <RadixTabs.Root value={value} onValueChange={onChange} className={className}>
      <RadixTabs.List className="tabs" aria-label="Detail sections">
        {tabs.map(([id, label]) => (
          <RadixTabs.Trigger key={id} className={cn('tab', value === id && 'active')} value={id}>
            {label}
          </RadixTabs.Trigger>
        ))}
      </RadixTabs.List>
    </RadixTabs.Root>
  );
}
