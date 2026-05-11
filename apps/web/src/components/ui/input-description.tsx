export function InputDescription({ children }: { children: React.ReactNode }) {
  return (
    <p className="mt-1 text-xs text-muted-foreground leading-relaxed">
      {children}
    </p>
  );
}
