using Evently.Common.Domain;
using Evently.Modules.Ticketing.Domain.Customers;
using Evently.Modules.Ticketing.Domain.Orders;
using Evently.Modules.Ticketing.UnitTests.Abstractions;
using FluentAssertions;

namespace Evently.Modules.Ticketing.UnitTests.Orders;

public class OrderTests : BaseTest
{
    private static Order CreateOrder()
    {
        var customer = Customer.Create(
            Guid.NewGuid(),
            Faker.Internet.Email(),
            Faker.Name.FirstName(),
            Faker.Name.LastName());

        return Order.Create(customer);
    }

    [Fact]
    public void Create_ShouldRaiseDomainEvent_WhenOrderIsCreated()
    {
        //Act
        Order order = CreateOrder();

        //Assert
        OrderCreatedDomainEvent domainEvent =
            AssertDomainEventWasPublished<OrderCreatedDomainEvent>(order);

        domainEvent.OrderId.Should().Be(order.Id);
    }

    [Fact]
    public void IssueTicket_ShouldReturnFailure_WhenTicketAlreadyIssued()
    {
        //Arrange
        Order order = CreateOrder();
        order.IssueTickets();

        //Act
        Result issueTicketsResult = order.IssueTickets();

        //Assert
        issueTicketsResult.Error.Should().Be(OrderErrors.TicketsAlreadyIssues);
    }

    [Fact]
    public void IssueTicket_ShouldRaiseDomainEvent_WhenTicketIsIssued()
    {
        //Arrange
        Order order = CreateOrder();

        //Act
        order.IssueTickets();

        //Assert
        OrderTicketsIssuedDomainEvent domainEvent =
            AssertDomainEventWasPublished<OrderTicketsIssuedDomainEvent>(order);

        domainEvent.OrderId.Should().Be(order.Id);
    }

    // Pay tests

    [Fact]
    public void Pay_ShouldSetStatusToPaid_WhenOrderIsPending()
    {
        //Arrange
        Order order = CreateOrder();

        //Act
        Result result = order.Pay();

        //Assert
        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Paid);
    }

    [Fact]
    public void Pay_ShouldRaiseDomainEvent_WhenOrderIsPaid()
    {
        //Arrange
        Order order = CreateOrder();

        //Act
        order.Pay();

        //Assert
        OrderPaidDomainEvent domainEvent =
            AssertDomainEventWasPublished<OrderPaidDomainEvent>(order);

        domainEvent.OrderId.Should().Be(order.Id);
    }

    [Fact]
    public void Pay_ShouldReturnFailure_WhenOrderIsNotPending()
    {
        //Arrange
        Order order = CreateOrder();
        order.Pay();

        //Act
        Result result = order.Pay();

        //Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(OrderErrors.NotPending);
    }

    // Refund tests

    [Fact]
    public void Refund_ShouldSetStatusToRefunded_WhenOrderIsPaid()
    {
        //Arrange
        Order order = CreateOrder();
        order.Pay();

        //Act
        Result result = order.Refund();

        //Assert
        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Refunded);
    }

    [Fact]
    public void Refund_ShouldRaiseDomainEvent_WhenOrderIsRefunded()
    {
        //Arrange
        Order order = CreateOrder();
        order.Pay();

        //Act
        order.Refund();

        //Assert
        OrderRefundedDomainEvent domainEvent =
            AssertDomainEventWasPublished<OrderRefundedDomainEvent>(order);

        domainEvent.OrderId.Should().Be(order.Id);
    }

    [Fact]
    public void Refund_ShouldReturnFailure_WhenOrderIsNotPaid()
    {
        //Arrange
        Order order = CreateOrder();

        //Act
        Result result = order.Refund();

        //Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(OrderErrors.NotPaid);
    }

    [Fact]
    public void Refund_ShouldBeIdempotent_WhenOrderIsAlreadyRefunded()
    {
        //Arrange
        Order order = CreateOrder();
        order.Pay();
        order.Refund();

        //Act
        Result result = order.Refund();

        //Assert
        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Refunded);
    }

    // Cancel tests

    [Fact]
    public void Cancel_ShouldSetStatusToCanceled_WhenOrderIsPending()
    {
        //Arrange
        Order order = CreateOrder();

        //Act
        Result result = order.Cancel();

        //Assert
        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Canceled);
    }

    [Fact]
    public void Cancel_ShouldRaiseDomainEvent_WhenOrderIsCanceled()
    {
        //Arrange
        Order order = CreateOrder();

        //Act
        order.Cancel();

        //Assert
        OrderCanceledDomainEvent domainEvent =
            AssertDomainEventWasPublished<OrderCanceledDomainEvent>(order);

        domainEvent.OrderId.Should().Be(order.Id);
    }

    [Fact]
    public void Cancel_ShouldReturnFailure_WhenOrderIsNotPending()
    {
        //Arrange
        Order order = CreateOrder();
        order.Pay();

        //Act
        Result result = order.Cancel();

        //Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(OrderErrors.NotPending);
    }

    [Fact]
    public void Cancel_ShouldBeIdempotent_WhenOrderIsAlreadyCanceled()
    {
        //Arrange
        Order order = CreateOrder();
        order.Cancel();

        //Act
        Result result = order.Cancel();

        //Assert
        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Canceled);
    }
}
