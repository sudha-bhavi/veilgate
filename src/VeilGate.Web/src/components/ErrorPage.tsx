import { Link } from "react-router-dom";

type Props = { code: string; title: string; message: string };

export default function ErrorPage({ code, title, message }: Props) {
  return (
    <main className="mx-auto max-w-xl p-6 text-center">
      <p className="text-4xl font-semibold text-muted">{code}</p>
      <h1 className="mt-2 text-xl font-semibold">{title}</h1>
      <p className="mt-2 text-muted">{message}</p>
      <Link
        to="/dashboard"
        className="mt-6 inline-flex rounded-lg bg-brand px-4 py-2 font-medium text-white hover:bg-brand-hover focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
      >
        Back to dashboard
      </Link>
    </main>
  );
}
