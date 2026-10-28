export default function Placeholder({ title }: { title: string }) {
  return (
    <main className="mx-auto max-w-xl p-6">
      <h1 className="text-xl font-semibold">{title}</h1>
      <p className="mt-2 text-muted">Coming in a later step.</p>
    </main>
  );
}
