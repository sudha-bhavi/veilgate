import Button from "./Button";

type Props = {
  page: number;
  pages: number;
  /** For example "12 clients". */
  summary: string;
  onPage: (page: number) => void;
};

export default function Pager({ page, pages, summary, onPage }: Props) {
  return (
    <nav aria-label="Pages" className="flex items-center justify-between">
      <p className="text-muted">
        {summary} · page {page} of {pages}
      </p>
      <div className="flex gap-2">
        <Button
          variant="secondary"
          disabled={page <= 1}
          onClick={() => onPage(page - 1)}
        >
          Previous
        </Button>
        <Button
          variant="secondary"
          disabled={page >= pages}
          onClick={() => onPage(page + 1)}
        >
          Next
        </Button>
      </div>
    </nav>
  );
}
