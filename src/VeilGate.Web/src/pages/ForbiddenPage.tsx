import ErrorPage from "../components/ErrorPage";

export default function ForbiddenPage() {
  return (
    <ErrorPage
      code="403"
      title="You are not allowed to open this page"
      message="Your role does not include access to this page. Ask a tenant admin if you think this is a mistake."
    />
  );
}
